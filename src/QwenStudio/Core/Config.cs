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
        /// <summary>
        /// Key in server_config.env with the vision projector (--mmproj). In profiles.json it means "this mode can see";
        /// a mode without it has the «Зрение» toggle disabled. On a launch variant it is set only when vision is on.
        /// </summary>
        public string Mmproj { get; set; }
        /// <summary>Default of the «Зрение» toggle ("vision": true in profiles.json).</summary>
        [System.Text.Json.Serialization.JsonPropertyName("vision")]
        public bool VisionByDefault { get; set; }
        /// <summary>Default of the «Размышления» toggle: the mode's args do not say --reasoning off.</summary>
        public bool ThinksByDefault { get { int i = Args.IndexOf("--reasoning"); return !(i >= 0 && i + 1 < Args.Count && Args[i + 1] == "off"); } }

        public bool Mtp => Args.Contains("draft-mtp");
        /// <summary>First name from --alias: the model id clients send; null when the profile sets none.</summary>
        public string Alias { get { int i = Args.IndexOf("--alias"); return i >= 0 && i + 1 < Args.Count ? Args[i + 1].Split(',')[0].Trim() : null; } }
        public int Slots { get { int i = Args.IndexOf("-np"); return i >= 0 && i + 1 < Args.Count && int.TryParse(Args[i + 1], out var n) ? n : 1; } }

        /// <summary>True for the variant that runs on the old model (FALLBACK_* paths).</summary>
        public bool Old { get; private set; }
        /// <summary>Launch variant: the vision projector is loaded.</summary>
        public bool Sees { get; private set; }
        /// <summary>Launch variant: reasoning is on.</summary>
        public bool Thinks { get; private set; } = true;

        /// <summary>
        /// What Start launches: this mode on the chosen model with the two toggles applied.
        /// Fallback model (FALLBACK_* paths, usually an older build and weights): no MTP head, at most 96K context and no prompt cache.
        /// Vision off drops --mmproj; vision with MTP turns the prompt cache off (the way «Зрение» was measured).
        /// Thinking off replaces the mode's --reasoning* flags with --reasoning off.
        /// </summary>
        public Profile Variant(bool old, bool vision, bool think)
        {
            bool sees = vision && !string.IsNullOrEmpty(Mmproj);
            var args = new List<string>();
            for (int i = 0; i < Args.Count; i++)
            {
                var a = Args[i];
                if (old && a.StartsWith("--spec-")) { i++; continue; }                 // flag + value
                if (a.StartsWith("--reasoning") && i + 1 < Args.Count)
                {
                    if (think) { args.Add(a); args.Add(a == "--reasoning" && Args[i + 1] == "off" ? "on" : Args[i + 1]); }
                    i++; continue;
                }
                args.Add(a);
            }
            if (!think) args.AddRange(new[] { "--reasoning", "off" });
            if ((old || sees && args.Contains("draft-mtp")) && !args.Contains("--no-cache-prompt")) args.Add("--no-cache-prompt");
            int ctx = old ? Math.Min(Ctx, 98304) : Ctx;
            var parts = (Badge ?? "").Split('·').Select(s => s.Trim())
                .Where(s => s != "" && s != "картинки" && s != "зрение" && !(old && s == "MTP"))
                .Select(s => s == $"{Ctx / 1024}K" ? $"{ctx / 1024}K" : s).ToList();
            if (sees) parts.Add("зрение");
            if (!think) parts.Add("без размышлений");
            return new Profile
            {
                Id = Id, Name = Name, Description = Description, Badge = string.Join(" · ", parts),
                Server = old ? "FALLBACK_SERVER_EXE" : Server, Model = old ? "FALLBACK_MODEL_PATH" : Model,
                Mmproj = sees ? Mmproj : null, VisionByDefault = VisionByDefault, Ctx = ctx, Args = args,
                Old = old, Sees = sees, Thinks = think,
            };
        }

        /// <summary>For the running line: the badge next to it already lists vision and thinking.</summary>
        public string ModelTitle => Name + (Old ? " · запасная модель" : "");
        public string Title => Name + (Sees ? " · зрение" : "") + (Thinks ? "" : " · без размышлений") + (Old ? " · запасная модель" : "");
        /// <summary>For the start button: the toggles are right above it.</summary>
        public string ShortTitle => Name;  // for the start button; the model is on the switch above, the full Title in its tooltip

        /// <summary>"chat|main|vision|nothink": what ran last, for autostart.</summary>
        public string Key => $"{Id}|{(Old ? "old" : "main")}|{(Sees ? "vision" : "")}|{(Thinks ? "" : "nothink")}";

        /// <summary>Modes that were separate cards before the toggles: id → (mode, vision, thinking).</summary>
        static readonly Dictionary<string, (string id, bool vision, bool think)> legacy = new()
        {
            ["nothink"] = ("chat", false, false),
            ["vision"] = ("chat", true, true),
            ["parallel-nothink"] = ("parallel", true, false),
        };

        /// <summary>The mode an id belongs to now: "nothink" → "chat"; anything else stays as is.</summary>
        public static string BaseId(string id) => id != null && legacy.TryGetValue(id, out var l) ? l.id : id;

        /// <summary>
        /// A launch variant by id; toggles not given come from the legacy card the id names, else from the mode's defaults.
        /// Null when the mode is gone from profiles.json.
        /// </summary>
        public static Profile Resolve(IList<Profile> all, string id, bool old, bool? vision = null, bool? think = null)
        {
            var p = all.FirstOrDefault(x => x.Id == id);
            if (p == null && id != null && legacy.TryGetValue(id, out var l))
            {
                p = all.FirstOrDefault(x => x.Id == l.id);
                vision ??= l.vision; think ??= l.think;
            }
            return p?.Variant(old, vision ?? p.VisionByDefault, think ?? p.ThinksByDefault);
        }

        /// <summary>Parses <see cref="Key"/>; also the older "chat|old" form.</summary>
        public static Profile FromKey(IList<Profile> all, string key)
        {
            var k = (key ?? "").Split('|');
            if (k[0] == "") return null;
            bool? vision = k.Length > 2 ? k[2] == "vision" : null, think = k.Length > 3 ? k[3] != "nothink" : null;
            return Resolve(all, k[0], k.Length > 1 && k[1] == "old", vision, think);
        }

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
