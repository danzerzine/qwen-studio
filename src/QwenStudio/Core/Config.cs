using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QwenStudio.Core
{
    /// <summary>Project folder: the first directory upwards from the exe that has server_config.env or profiles.json.</summary>
    public static class Paths
    {
        public static readonly string Base = Find();
        public static string Logs => Dir(Path.Combine(Base, "logs", "studio"));
        public static string State => Path.Combine(Logs, "state.json");
        public static string ServerConfig => Path.Combine(Base, "server_config.env");
        public static string Profiles => Path.Combine(Base, "profiles.json");
        public static string LocalBin => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
        public static string LmsExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "lm-studio", "bin", "lms.exe");
        public static string OpencodeDesktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "@opencode-aidesktop", "OpenCode.exe");
        public static string OpencodeConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.jsonc");

        /// <summary>llama-bNNNN\llama-server.exe with the highest build number next to the exe (what «Обновления» downloads).</summary>
        public static string NewestLlama() => Directory.GetDirectories(Base, "llama-b*")
            .Select(d => (d, n: int.TryParse(Path.GetFileName(d)[7..], out var n) ? n : -1))
            .OrderByDescending(x => x.n)
            .Select(x => Directory.GetFiles(x.d, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault())
            .FirstOrDefault(f => f != null) ?? "";

        public static string Resolve(string p) => string.IsNullOrWhiteSpace(p) ? "" : Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Base, p));

        static string Dir(string d) { Directory.CreateDirectory(d); return d; }

        static string Find()
        {
            var d = AppContext.BaseDirectory;
            for (var cur = new DirectoryInfo(d); cur != null; cur = cur.Parent)
                if (File.Exists(Path.Combine(cur.FullName, "server_config.env")) || File.Exists(Path.Combine(cur.FullName, "profiles.json")))
                    return cur.FullName;
            return d.TrimEnd('\\');
        }
    }

    /// <summary>KEY=VALUE file. Tolerates a BOM and duplicate keys (last one wins, duplicates are dropped on save).</summary>
    public sealed class EnvFile
    {
        readonly string path;
        readonly List<KeyValuePair<string, string>> items = new();

        public EnvFile(string path) { this.path = path; Load(); }

        public void Load()
        {
            items.Clear();
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var l = raw.TrimStart('﻿').Trim();
                int i = l.IndexOf('=');
                if (i <= 0 || l.StartsWith("#")) continue;
                Put(l[..i].Trim(), l[(i + 1)..].Trim().Trim('"'));
            }
        }

        public string Get(string key, string def = "")
        {
            var v = items.FirstOrDefault(p => p.Key == key).Value;
            return string.IsNullOrEmpty(v) ? def : v;
        }

        public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;

        public void Set(string key, string value) { Put(key, value); Save(); }

        void Put(string key, string value)
        {
            int idx = items.FindIndex(p => p.Key == key);
            if (idx >= 0) items[idx] = new(key, value); else items.Add(new(key, value));
        }

        void Save() => File.WriteAllLines(path, items.Select(p => p.Key + "=" + p.Value), new UTF8Encoding(false));
    }

    public sealed class Profile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Badge { get; set; }
        public string Description { get; set; }
        public string Server { get; set; } = "SERVER_EXE";
        public string Model { get; set; } = "MODEL_PATH";
        public int Ctx { get; set; }
        public List<string> Args { get; set; } = new();
        /// <summary>Key in server_config.env with the vision projector (--mmproj); empty for text-only modes.</summary>
        public string Mmproj { get; set; }

        public bool Mtp => Args.Contains("draft-mtp");
        /// <summary>First name from --alias: the model id clients send; null when the profile sets none.</summary>
        public string Alias { get { int i = Args.IndexOf("--alias"); return i >= 0 && i + 1 < Args.Count ? Args[i + 1].Split(',')[0].Trim() : null; } }
        public int Slots { get { int i = Args.IndexOf("-np"); return i >= 0 && i + 1 < Args.Count && int.TryParse(Args[i + 1], out var n) ? n : 1; } }

        /// <summary>True for the variant that runs on the old model (FALLBACK_* paths).</summary>
        public bool Old { get; private set; }

        /// <summary>
        /// The same mode on the fallback model (FALLBACK_* paths, usually an older build and weights): no MTP head,
        /// at most 96K context and no prompt cache.
        /// </summary>
        public Profile For(bool old)
        {
            if (!old || Old) return this;
            var args = new List<string>();
            for (int i = 0; i < Args.Count; i++)
            {
                if (Args[i].StartsWith("--spec-")) { i++; continue; }      // flag + value
                args.Add(Args[i]);
            }
            if (!args.Contains("--no-cache-prompt")) args.Add("--no-cache-prompt");
            int ctx = Math.Min(Ctx, 98304);
            var badge = string.Join(" · ", (Badge ?? "").Split('·').Select(s => s.Trim()).Where(s => s != "MTP" && s != ""));
            if (ctx != Ctx) badge = badge.Replace($"{Ctx / 1024}K", $"{ctx / 1024}K");
            return new Profile
            {
                Id = Id, Name = Name, Description = Description, Badge = badge,
                Server = "FALLBACK_SERVER_EXE", Model = "FALLBACK_MODEL_PATH", Mmproj = Mmproj, Ctx = ctx, Args = args, Old = true,
            };
        }

        public string Title => Old ? Name + " · запасная модель" : Name;

        public static List<Profile> LoadAll()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var doc = JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(Paths.Profiles), opts);
            return doc?.Profiles ?? new();
        }

        sealed class ProfilesFile { public List<Profile> Profiles { get; set; } }
    }

    public sealed class Settings
    {
        public readonly EnvFile Env = new(Paths.ServerConfig);

        public string ApiKey => Env.Get("LLAMA_API_KEY");
        public string Host => Env.Get("HOST", "0.0.0.0");
        public int Port => Env.GetInt("PORT", 8080);
        public int WebUiPort => Env.GetInt("WEBUI_PORT", 3000);
        string lanIp;
        public string LanIp => lanIp ??= PickLanIp();

        string PickLanIp()
        {
            var ips = Net.LanIps();
            var cfg = Env.Get("LAN_IP");
            return ips.Contains(cfg) ? cfg : ips.FirstOrDefault() ?? (cfg == "" ? "127.0.0.1" : cfg);
        }

        /// <summary>The profile's server; the fallback slot falls back to SERVER_EXE, and that to the newest llama-bNNNN folder.</summary>
        public string ServerExe(Profile p) => Paths.Resolve(Env.Get(p.Server, Env.Get("SERVER_EXE", Paths.NewestLlama())));
        public string ModelFile(Profile p) => Paths.Resolve(Env.Get(p.Model));
        public string MmprojFile(Profile p) => string.IsNullOrEmpty(p.Mmproj) ? null : Paths.Resolve(Env.Get(p.Mmproj));

        public static string NewApiKey() => "qwen-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
    }

    public static class Net
    {
        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        /// <summary>PID of the process listening on this TCP port (IPv4), or 0.</summary>
        public static int PortOwner(int port)
        {
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return 0;
                int n = System.Runtime.InteropServices.Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid — six DWORDs
                    var row = buf + 4 + i * 24;
                    int p = System.Runtime.InteropServices.Marshal.ReadInt32(row + 8);
                    if ((((p & 0xFF) << 8) | ((p >> 8) & 0xFF)) == port)
                        return System.Runtime.InteropServices.Marshal.ReadInt32(row + 20);
                }
                return 0;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }

        public static List<string> LanIps()
        {
            var res = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var n = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    if (n.Contains("virtual") || n.Contains("vethernet") || n.Contains("hyper-v") || n.Contains("wsl")) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var s = a.Address.ToString();
                            if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172.")) res.Add(s);
                        }
                }
            }
            catch { }
            return res;
        }
    }
}
