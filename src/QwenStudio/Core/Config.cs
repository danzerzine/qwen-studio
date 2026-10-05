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

    /// <summary>
    /// KEY=VALUE file. Tolerates a BOM and duplicate keys (last one wins). The file is shared with .bat and eval scripts,
    /// so a save re-reads it first, changes only the line of that key (comments, blank lines and the other lines stay
    /// as they are) and swaps the file in whole: a crash mid-write can never leave it empty.
    /// </summary>
    public sealed class EnvFile
    {
        readonly string path;
        readonly Dictionary<string, string> items = new();
        List<string> lines = new();
        bool bom, crlf = true;

        public EnvFile(string path) { this.path = path; Load(); }

        public void Load()
        {
            items.Clear();
            lines = new();
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            bom = text.StartsWith('﻿');
            crlf = text.Contains("\r\n") || !text.Contains('\n');
            lines = text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            foreach (var l in lines)
                if (KeyOf(l) is string k) items[k] = l[(l.IndexOf('=') + 1)..].Trim().Trim('"');
        }

        static string KeyOf(string line)
        {
            var l = line.Trim();
            int i = l.IndexOf('=');
            return i <= 0 || l.StartsWith("#") ? null : l[..i].Trim();
        }

        public string Get(string key, string def = "") => items.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : def;

        public int GetInt(string key, int def) => int.TryParse(Get(key), out var v) ? v : def;

        public void Set(string key, string value)
        {
            Load();                                      // keep what scripts or a text editor wrote since we last read it
            int last = lines.FindLastIndex(l => KeyOf(l) == key);
            if (last >= 0) lines[last] = key + "=" + value; else lines.Add(key + "=" + value);
            for (int i = last - 1; i >= 0; i--)          // older duplicates would shadow nothing but confuse a reader
                if (KeyOf(lines[i]) == key) lines.RemoveAt(i);
            items[key] = value;
            Save();
        }

        void Save()
        {
            var tmp = path + ".tmp";
            var nl = crlf ? "\r\n" : "\n";
            File.WriteAllText(tmp, string.Join(nl, lines) + nl, new UTF8Encoding(bom));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
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

        /// <summary>Which model a launch variant runs on: "main", "old" (FALLBACK_* paths) or "uncensored" (UNCENSORED_MODEL_PATH).</summary>
        public string Slot { get; private set; } = MainSlot;
        public const string MainSlot = "main", OldSlot = "old", UncensoredSlot = "uncensored";
        /// <summary>Slot from a stored string; anything unknown is the main model.</summary>
        public static string ParseSlot(string s) => s is OldSlot or UncensoredSlot ? s : MainSlot;
        /// <summary>server_config.env key with the model file of a slot.</summary>
        public static string ModelKeyOf(string slot) => slot switch { OldSlot => "FALLBACK_MODEL_PATH", UncensoredSlot => "UNCENSORED_MODEL_PATH", _ => "MODEL_PATH" };
        /// <summary>" · запасная модель" / " · без цензуры" after a mode name; empty for the main model.</summary>
        public static string SlotSuffix(string slot) => slot switch { OldSlot => L.T(" · запасная модель"), UncensoredSlot => L.T(" · без цензуры"), _ => "" };

        /// <summary>True for the variant that runs on the fallback model (FALLBACK_* paths).</summary>
        public bool Old => Slot == OldSlot;
        /// <summary>Launch variant: the vision projector is loaded.</summary>
        public bool Sees { get; private set; }
        /// <summary>Launch variant: reasoning is on.</summary>
        public bool Thinks { get; private set; } = true;

        /// <summary>
        /// What Start launches: this mode on the chosen model with the two toggles applied.
        /// Fallback model (FALLBACK_* paths, usually an older build and weights): no MTP head, at most 96K context and no prompt cache.
        /// Uncensored model — a fine-tune of the main model (same architecture, MTP head, server and flags), only the weights differ.
        /// Vision off drops --mmproj; vision with MTP turns the prompt cache off (the way «Зрение» was measured).
        /// Thinking off replaces the mode's --reasoning* flags with --reasoning off.
        /// </summary>
        public Profile Variant(string slot, bool vision, bool think)
        {
            slot = ParseSlot(slot);
            bool old = slot == OldSlot;
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
            if (sees) parts.Add(L.T("зрение"));
            if (!think) parts.Add(L.T("без размышлений"));
            return new Profile
            {
                Id = Id, Name = Name, Description = Description, Badge = string.Join(" · ", parts),
                Server = old ? "FALLBACK_SERVER_EXE" : Server,
                Model = slot == MainSlot ? Model : ModelKeyOf(slot),
                Mmproj = sees ? Mmproj : null, VisionByDefault = VisionByDefault, Ctx = ctx, Args = args,
                Slot = slot, Sees = sees, Thinks = think,
            };
        }

        /// <summary>For the running line: the badge next to it already lists vision and thinking.</summary>
        public string ModelTitle => L.T(Name) + SlotSuffix(Slot);
        public string Title => L.T(Name) + (Sees ? L.T(" · зрение") : "") + (Thinks ? "" : L.T(" · без размышлений")) + SlotSuffix(Slot);
        /// <summary>For the start button: the toggles are right above it.</summary>
        public string ShortTitle => L.T(Name);  // for the start button; the model is on the switch above, the full Title in its tooltip

        /// <summary>"chat|main|vision|nothink" (or chat|old|…, chat|uncensored|…): what ran last, for autostart.</summary>
        public string Key => $"{Id}|{Slot}|{(Sees ? "vision" : "")}|{(Thinks ? "" : "nothink")}";

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
        public static Profile Resolve(IList<Profile> all, string id, string slot, bool? vision = null, bool? think = null)
        {
            var p = all.FirstOrDefault(x => x.Id == id);
            if (p == null && id != null && legacy.TryGetValue(id, out var l))
            {
                p = all.FirstOrDefault(x => x.Id == l.id);
                vision ??= l.vision; think ??= l.think;
            }
            return p?.Variant(slot, vision ?? p.VisionByDefault, think ?? p.ThinksByDefault);
        }

        /// <summary>Parses <see cref="Key"/>; also the older "chat|old" form.</summary>
        public static Profile FromKey(IList<Profile> all, string key)
        {
            var k = (key ?? "").Split('|');
            if (k[0] == "") return null;
            bool? vision = k.Length > 2 ? k[2] == "vision" : null, think = k.Length > 3 ? k[3] != "nothink" : null;
            return Resolve(all, k[0], k.Length > 1 ? k[1] : null, vision, think);
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
        /// <summary>Listen address; without HOST only this computer (the LAN switch in Settings writes 0.0.0.0).</summary>
        public string Host => Env.Get("HOST", "127.0.0.1");
        /// <summary>The server is reachable from other computers.</summary>
        public bool OnLan => !(Host is "127.0.0.1" or "localhost" or "::1");
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
