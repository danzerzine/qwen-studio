using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QwenStudio.Core
{
    /// <summary>Small helpers for running console tools without a window.</summary>
    public static class Proc
    {
        public static async Task<(int code, string output)> Run(string exe, IEnumerable<string> args, int timeoutMs = 30000, Action<string> onLine = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = Paths.LocalBin + ";" + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var sb = new StringBuilder();
            try
            {
                using var p = new Process { StartInfo = psi };
                void On(string s) { if (s == null) return; lock (sb) sb.AppendLine(s); onLine?.Invoke(s); }
                p.OutputDataReceived += (_, e) => On(e.Data);
                p.ErrorDataReceived += (_, e) => On(e.Data);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var done = await Task.Run(() => p.WaitForExit(timeoutMs));
                if (!done) { try { p.Kill(true); } catch { } return (-1, sb.ToString()); }
                p.WaitForExit();
                return (p.ExitCode, sb.ToString());
            }
            catch (Exception e) { return (-2, e.Message); }
        }

        /// <summary>Runs a command line elevated (UAC prompt). Returns false if the user declined.</summary>
        public static async Task<bool> RunElevated(string cmdLine)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + cmdLine) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                await Task.Run(() => p.WaitForExit(60000));
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        public static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;

        public static void KillAll(string name)
        {
            foreach (var p in Process.GetProcessesByName(name)) { try { p.Kill(true); } catch { } }
        }
    }

    /// <summary>Other GPU tenants that must be empty before our server starts.</summary>
    public static class Neighbours
    {
        public static async Task<List<string>> OllamaModels()
        {
            var res = new List<string>();
            var json = await Http.Get("http://127.0.0.1:11434/api/ps");
            if (json == null) return res;
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var m in d.RootElement.GetProperty("models").EnumerateArray())
                    res.Add(m.GetProperty("name").GetString());
            }
            catch { }
            return res;
        }

        public static async Task UnloadOllama(IEnumerable<string> models)
        {
            foreach (var m in models)
                await Http.Post("http://127.0.0.1:11434/api/generate", JsonSerializer.Serialize(new { model = m, keep_alive = 0 }));
            for (int i = 0; i < 20 && (await OllamaModels()).Count > 0; i++) await Task.Delay(500);
        }

        public static async Task<bool> LmStudioBusy() => await Http.Status("http://127.0.0.1:1234/v1/models") == 200;

        public static async Task UnloadLmStudio()
        {
            if (File.Exists(Paths.LmsExe)) await Proc.Run(Paths.LmsExe, new[] { "unload", "--all" }, 20000);
        }
    }

    public sealed class WebUi
    {
        readonly Settings cfg;
        public WebUi(Settings cfg) { this.cfg = cfg; }

        public string Exe => Path.Combine(Paths.LocalBin, "open-webui.exe");
        public string LogFile => Path.Combine(Paths.Logs, "webui.log");
        public bool Installed => File.Exists(Exe);
        /// <summary>Only our open-webui.exe: another Open WebUI install may run the same exe name from its own folder.</summary>
        public bool Running => Mine().Length > 0;
        public string Url => $"http://127.0.0.1:{cfg.WebUiPort}";

        public void Start()
        {
            SyncKey();
            var secretFile = Path.Combine(Paths.Base, ".webui_secret_key");
            if (!File.Exists(secretFile))
                File.WriteAllText(secretFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var secret = File.ReadAllText(secretFile).Trim().TrimStart('﻿');

            // cmd is used only for the "> file" redirect, so the WebUI keeps logging after this window closes
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Paths.Base,
                Arguments = $"/d /c \"\"{Exe}\" serve --host 0.0.0.0 --port {cfg.WebUiPort} > \"{LogFile}\" 2>&1\"",
            };
            var env = psi.Environment;
            env["PATH"] = Paths.LocalBin + ";" + Environment.GetEnvironmentVariable("PATH");
            env["PYTHONIOENCODING"] = "utf-8";
            env["OPENAI_API_BASE_URLS"] = $"http://127.0.0.1:{cfg.Port}/v1";
            env["OPENAI_API_KEYS"] = string.IsNullOrEmpty(cfg.ApiKey) ? "none" : cfg.ApiKey;
            env["ENABLE_OLLAMA_API"] = "False";
            env["WEBUI_AUTH"] = "False";
            env["WEBUI_SECRET_KEY"] = secret;
            env["ENABLE_WEB_SEARCH"] = "True";
            env["WEB_SEARCH_ENGINE"] = "duckduckgo";
            env["WEB_SEARCH_RESULT_COUNT"] = "5";
            Process.Start(psi);
        }

        public Task Stop() => Task.Run(() => { foreach (var p in Mine()) { try { p.Kill(true); } catch { } } });

        Process[] Mine() => Process.GetProcessesByName("open-webui").Where(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToArray();

        /// <summary>
        /// Open WebUI saves OPENAI_API_KEYS into webui.db on its first start and ignores the variable afterwards,
        /// so after a key change it kept the old key and showed no models (401). Before each start the current key
        /// is written into its local connections (LM Studio keeps its own key). Uses Open WebUI's own Python and sqlite3.
        /// Returns how many connections were updated, or null if it could not check.
        /// </summary>
        public int? SyncKey()
        {
            var py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uv", "tools", "open-webui", "Scripts", "python.exe");
            if (string.IsNullOrEmpty(cfg.ApiKey) || !File.Exists(py)) return null;
            const string script = @"
import os, json, sqlite3, shutil, time, importlib.util, urllib.parse
d = os.environ.get('DATA_DIR') or os.path.join(importlib.util.find_spec('open_webui').submodule_search_locations[0], 'data')
db = os.path.join(d, 'webui.db')
if not os.path.exists(db): print(0); raise SystemExit
c = sqlite3.connect(db, timeout=10)
u = c.execute(""select value from config where key='openai.api_base_urls'"").fetchone()
k = c.execute(""select value from config where key='openai.api_keys'"").fetchone()
if not u or not k: print(0); raise SystemExit
urls, keys = json.loads(u[0]), json.loads(k[0])
keys += [''] * (len(urls) - len(keys))
key, skip, n = os.environ['QS_KEY'], int(os.environ['QS_SKIP_PORT']), 0
for i, url in enumerate(urls):
    p = urllib.parse.urlparse(url)
    if p.hostname in ('127.0.0.1', 'localhost') and p.port != skip and keys[i] != key:
        keys[i] = key; n += 1
if n:
    bak = db + '.bak-' + time.strftime('%Y%m%d')
    if not os.path.exists(bak): c.close(); shutil.copy2(db, bak); c = sqlite3.connect(db, timeout=10)
    c.execute(""update config set value=?, updated_at=? where key='openai.api_keys'"", (json.dumps(keys), int(time.time())))
    c.commit()
print(n)
";
            try
            {
                var psi = new ProcessStartInfo(py) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(script);
                psi.Environment["QS_KEY"] = cfg.ApiKey;      // via the environment: never on a command line
                psi.Environment["QS_SKIP_PORT"] = cfg.Env.GetInt("LMSTUDIO_PORT", 1234).ToString();
                using var p = Process.Start(psi);
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
                return p.ExitCode == 0 && int.TryParse(output.Trim(), out var n) ? n : null;
            }
            catch { return null; }
        }
    }

    public sealed class Release
    {
        public string Tag, ZipUrl, CudartUrl;
        public int Build;
        public long Size;
    }

    public static class Updates
    {
        static readonly Regex rxBuild = new(@"b(\d{4,6})", RegexOptions.Compiled);

        public static async Task<int?> LocalLlamaBuild(string exe)
        {
            var m = rxBuild.Match(Path.GetFileName(Path.GetDirectoryName(exe) ?? ""));
            if (m.Success) return int.Parse(m.Groups[1].Value);
            if (!File.Exists(exe)) return null;
            var (_, o) = await Proc.Run(exe, new[] { "--version" }, 15000);
            var v = Regex.Match(o, @"build\s*[:=]?\s*(\d+)");
            return v.Success ? int.Parse(v.Groups[1].Value) : null;
        }

        public static async Task<Release> LatestLlama()
        {
            var json = await Http.Get("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", longTimeout: true);
            if (json == null) return null;
            using var d = JsonDocument.Parse(json);
            foreach (var rel in d.RootElement.EnumerateArray())
            {
                var r = new Release { Tag = rel.GetProperty("tag_name").GetString() };
                foreach (var a in rel.GetProperty("assets").EnumerateArray())
                {
                    var n = a.GetProperty("name").GetString();
                    var u = a.GetProperty("browser_download_url").GetString();
                    if (n.StartsWith("llama-b") && n.Contains("bin-win-cuda-12") && n.EndsWith("-x64.zip")) { r.ZipUrl = u; r.Size += a.GetProperty("size").GetInt64(); }
                    else if (n.StartsWith("cudart-") && n.Contains("win-cuda-12") && n.EndsWith("-x64.zip")) { r.CudartUrl = u; r.Size += a.GetProperty("size").GetInt64(); }
                }
                var m = rxBuild.Match(r.Tag ?? "");
                if (r.ZipUrl != null && m.Success) { r.Build = int.Parse(m.Groups[1].Value); return r; }
            }
            return null;
        }

        /// <summary>Downloads the CUDA build and its cudart DLLs into a fresh llama-bNNNN folder. Returns the new server path.</summary>
        public static async Task<string> InstallLlama(Release r, IProgress<string> progress)
        {
            var target = Path.Combine(Paths.Base, $"llama-b{r.Build}");
            var tmp = Path.Combine(Path.GetTempPath(), "QwenStudio");
            Directory.CreateDirectory(tmp);
            foreach (var url in new[] { r.ZipUrl, r.CudartUrl }.Where(u => u != null))
            {
                var zip = Path.Combine(tmp, Path.GetFileName(new Uri(url).LocalPath));
                progress.Report("Скачиваю " + Path.GetFileName(zip));
                using (var resp = await Http.Slow.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0, done = 0, lastReport = 0;
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(zip);
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        done += n;
                        if (done - lastReport > 8 << 20) { lastReport = done; progress.Report($"{Path.GetFileName(zip)}: {done >> 20} / {total >> 20} МБ"); }
                    }
                }
                progress.Report("Распаковываю " + Path.GetFileName(zip));
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true));
                try { File.Delete(zip); } catch { }
            }
            var exe = Directory.GetFiles(target, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            return exe == null ? null : Path.GetRelativePath(Paths.Base, exe);
        }

        public static async Task<string> LocalWebUi()
        {
            var uv = Path.Combine(Paths.LocalBin, "uv.exe");
            if (!File.Exists(uv)) return null;
            var (_, o) = await Proc.Run(uv, new[] { "tool", "list" }, 20000);
            var m = Regex.Match(o, @"open-webui\s+v?([0-9][0-9.]*)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static async Task<string> LatestWebUi()
        {
            var json = await Http.Get("https://pypi.org/pypi/open-webui/json", longTimeout: true);
            if (json == null) return null;
            try { using var d = JsonDocument.Parse(json); return d.RootElement.GetProperty("info").GetProperty("version").GetString(); }
            catch { return null; }
        }

        public static Task<(int code, string output)> UpgradeWebUi(Action<string> onLine) =>
            Proc.Run(Path.Combine(Paths.LocalBin, "uv.exe"), new[] { "tool", "upgrade", "open-webui" }, 15 * 60000, onLine);
    }

    public static class Firewall
    {
        public static readonly (string name, Func<Settings, int> port)[] Rules =
        {
            ("Qwen Studio llama-server LAN", s => s.Port),
            ("Qwen Studio Open WebUI LAN", s => s.WebUiPort),
        };

        public static async Task<bool> RuleExists(string name)
        {
            var (code, _) = await Proc.Run("netsh", new[] { "advfirewall", "firewall", "show", "rule", "name=" + name }, 10000);
            return code == 0;
        }

        public static async Task<bool> AllPresent(Settings s)
        {
            foreach (var r in Rules) if (!await RuleExists(r.name)) return false;
            return true;
        }

        public static Task<bool> Add(Settings s)
        {
            var parts = Rules.Select(r =>
                $"netsh advfirewall firewall delete rule name=\"{r.name}\" >nul 2>&1 & " +
                $"netsh advfirewall firewall add rule name=\"{r.name}\" dir=in action=allow protocol=TCP localport={r.port(s)} profile=private remoteip=localsubnet");
            return Proc.RunElevated("\"" + string.Join(" & ", parts) + "\"");
        }
    }

    public static class Power
    {
        public static Task<bool> Apply(int watts)
        {
            var cmd = $"nvidia-smi -pl {watts} & schtasks /Create /TN \"NVIDIA-PowerLimit-{watts}W\" /TR \"nvidia-smi -pl {watts}\" " +
                      "/SC ONSTART /RU SYSTEM /RL HIGHEST /DELAY 0001:00 /F";
            return Proc.RunElevated("\"" + cmd + "\"");
        }
    }

    public static class Keys
    {
        /// <summary>Writes a new key to server_config.env and replaces the old one in the opencode config if it is there.</summary>
        public static (string key, bool opencode) Rotate(Settings cfg)
        {
            var old = cfg.ApiKey;
            var key = Settings.NewApiKey();
            cfg.Env.Set("LLAMA_API_KEY", key);
            bool oc = false;
            try
            {
                if (!string.IsNullOrEmpty(old) && File.Exists(Paths.OpencodeConfig))
                {
                    var text = File.ReadAllText(Paths.OpencodeConfig);
                    if (text.Contains(old))
                    {
                        File.Copy(Paths.OpencodeConfig, Paths.OpencodeConfig + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                        File.WriteAllText(Paths.OpencodeConfig, text.Replace(old, key), new UTF8Encoding(false));
                        oc = true;
                    }
                }
            }
            catch { }
            return (key, oc);
        }

        public static string Mask(string k) => string.IsNullOrEmpty(k) ? "— не задан —" : k.Length <= 10 ? "••••••" : k[..5] + "••••••••••••" + k[^4..];
    }
}
