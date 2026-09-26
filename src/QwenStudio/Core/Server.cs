using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QwenStudio.Core
{
    public enum LogKind { Server, Info, Good, Warn, Error }

    public sealed class LogLine
    {
        public string Time { get; init; }
        public string Text { get; init; }
        public LogKind Kind { get; init; }
        public bool Important { get; init; }
        /// <summary>A line of llama-server output (coloured by content), not a message of Studio itself.</summary>
        public bool FromServer { get; init; }
    }

    public enum ServerState { Stopped, Starting, Running, Crashed, External }

    public static class Http
    {
        static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
        static readonly HttpClient slow = new() { Timeout = TimeSpan.FromSeconds(20) };
        static Http()
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QwenStudio/1.2");
            slow.DefaultRequestHeaders.UserAgent.ParseAdd("QwenStudio/1.2");
        }

        public static HttpClient Slow => slow;

        /// <summary>HTTP status code, or 0 when nothing answers.</summary>
        public static async Task<int> Status(string url)
        {
            try { using var r = await client.GetAsync(url); return (int)r.StatusCode; }
            catch { return 0; }
        }

        public static async Task<string> Get(string url, bool longTimeout = false)
        {
            try { return await (longTimeout ? slow : client).GetStringAsync(url); }
            catch { return null; }
        }

        /// <summary>GET with a bearer key (the key goes in the header, never in the URL); null on any failure.</summary>
        public static async Task<string> GetAuth(string url, string key)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                using var r = await client.SendAsync(req);
                return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
            }
            catch { return null; }
        }

        public static async Task Post(string url, string json)
        {
            try { using var r = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json")); }
            catch { }
        }
    }

    /// <summary>
    /// Runs llama-server as an independent process that writes its own --log-file.
    /// The app only tails that file, so closing the window never kills the server and a restarted app re-attaches.
    /// </summary>
    public sealed class ServerManager
    {
        public event Action<LogLine> Line;
        public event Action CudaError;
        public event Action Exited;
        /// <summary>A request finished: wall-clock time and generated tokens.</summary>
        public event Action<DateTime, int> Completed;
        /// <summary>A request was refused for a wrong API key.</summary>
        public event Action<DateTime> Rejected;

        public ServerState State { get; private set; } = ServerState.Stopped;
        public Profile Profile { get; private set; }
        public DateTime Since { get; private set; }
        public int Pid { get; private set; }
        public string LogFile { get; private set; }
        public int Port { get; private set; }
        /// <summary>True when this session picked up a server started earlier, not launched it.</summary>
        public bool Attached { get; private set; }
        /// <summary>llama.cpp build of the running server ("b11177"), from its folder name.</summary>
        public string Build { get; private set; }
        public string ExitInfo { get; private set; }
        public DateTime? LastDone { get; private set; }

        public double? PromptTps, GenTps, DraftAccept;

        Process proc;
        CancellationTokenSource tail;
        bool stopping;
        DateTime countFrom;      // lines before this moment were replayed from the file, not new
        int lastGenTokens;

        sealed class StateFile
        {
            public int Pid { get; set; }
            public string Profile { get; set; }
            public string Log { get; set; }
            public DateTime Started { get; set; }
            public int Port { get; set; }
            public bool Old { get; set; }
        }

        public void Emit(string text, LogKind kind = LogKind.Info) =>
            Line?.Invoke(new LogLine { Time = DateTime.Now.ToString("HH:mm:ss"), Text = text, Kind = kind, Important = true });

        /// <summary>Pick up a server that is already running (started by an earlier session or by hand).</summary>
        public void Attach(IList<Profile> profiles, int port)
        {
            try
            {
                var st = File.Exists(Paths.State) ? JsonSerializer.Deserialize<StateFile>(File.ReadAllText(Paths.State)) : null;
                var mine = st == null ? null : Process.GetProcessesByName("llama-server").FirstOrDefault(p => p.Id == st.Pid);
                if (mine == null) TryDelete(Paths.State);
                else
                {
                    Watch(mine);
                    Profile = profiles.FirstOrDefault(p => p.Id == st.Profile)?.For(st.Old);
                    LogFile = st.Log; Since = st.Started; Port = st.Port;
                    State = ServerState.Starting;          // the health poll promotes it to Running
                    Attached = true;
                    countFrom = DateTime.Now;
                    StartTail(fromEnd: true);
                    Emit($"Подключился к работающему серверу: {Profile?.Title ?? st.Profile}, PID {Pid}");
                    return;
                }
            }
            catch { }
            // llama-servers on other ports are someone else's business; only the one on our port matters
            if (PortOwnerName(port) is not "llama-server") return;
            Pid = Net.PortOwner(port);
            Port = port;
            State = ServerState.External;
            Attached = true;
            try { Build = BuildOf(Process.GetProcessById(Pid).MainModule?.FileName); } catch { Build = null; }
            Emit($"На порту {port} работает llama-server, запущенный не отсюда (PID {Pid}). Его можно остановить кнопкой «Остановить».", LogKind.Warn);
        }

        /// <summary>Process name of whoever listens on the port, or null if it is free.</summary>
        public static string PortOwnerName(int port)
        {
            int pid = Net.PortOwner(port);
            if (pid == 0) return null;
            try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; }
        }

        public void Start(Profile p, Settings cfg)
        {
            string exe = cfg.ServerExe(p), model = cfg.ModelFile(p);
            if (!File.Exists(exe)) throw new FileNotFoundException($"Не найден сервер: {exe}");
            if (!File.Exists(model)) throw new FileNotFoundException($"Не найдена модель: {model}");
            string mmproj = cfg.MmprojFile(p);
            if (mmproj != null && !File.Exists(mmproj)) throw new FileNotFoundException($"Не найден модуль зрения: {mmproj}");

            Port = cfg.Port;
            LogFile = Path.Combine(Paths.Logs, $"server-{p.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            foreach (var a in p.Args) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-m", model, "-c", p.Ctx.ToString(), "--host", cfg.Host, "--port", Port.ToString(),
                                      "--log-file", LogFile, "--log-colors", "off" })
                psi.ArgumentList.Add(a);
            if (mmproj != null) { psi.ArgumentList.Add("--mmproj"); psi.ArgumentList.Add(mmproj); }
            // the key goes through the environment: it never shows up in the process list or in the log
            if (string.IsNullOrEmpty(cfg.ApiKey)) psi.Environment.Remove("LLAMA_API_KEY");
            else psi.Environment["LLAMA_API_KEY"] = cfg.ApiKey;

            PromptTps = GenTps = DraftAccept = null;
            stopping = false;
            Attached = false;
            ExitInfo = null;
            countFrom = DateTime.MinValue;
            Build = BuildOf(exe);
            var pr = Process.Start(psi);
            Watch(pr);
            Profile = p;
            Since = DateTime.Now;
            State = ServerState.Starting;
            File.WriteAllText(Paths.State, JsonSerializer.Serialize(new StateFile { Pid = Pid, Profile = p.Id, Log = LogFile, Started = Since, Port = Port, Old = p.Old }));
            Emit($"▶ {p.Title} ({p.Badge}) · {Path.GetFileName(model)} · {Path.GetFileName(Path.GetDirectoryName(exe))} · PID {Pid}");
            StartTail(fromEnd: false);
        }

        public void MarkRunning() { if (State == ServerState.Starting) State = ServerState.Running; }

        /// <summary>"b11177" from ...\llama-b11177\llama-server.exe; the folder name otherwise.</summary>
        public static string BuildOf(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            var dir = Path.GetFileName(Path.GetDirectoryName(exe)) ?? "";
            var m = Regex.Match(dir, @"b(\d{3,6})");
            return m.Success ? "b" + m.Groups[1].Value : dir;
        }

        /// <summary>Stops our server (or the foreign one on our port) and nothing else.</summary>
        public async Task Stop()
        {
            stopping = true;
            tail?.Cancel();
            int pid = Pid;
            await Task.Run(() =>
            {
                if (pid == 0) return;
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p.ProcessName != "llama-server") return;       // PID reused by something else
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(8000);
                }
                catch { }
            });
            TryDelete(Paths.State);
            State = ServerState.Stopped;
            Profile = null;
            proc = null;
            Pid = 0;
            Attached = false;
            Build = null;
        }

        void Watch(Process p)
        {
            proc = p;
            Pid = p.Id;
            try { Build ??= BuildOf(p.MainModule?.FileName); } catch { }
            try
            {
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) =>
                {
                    if (stopping || proc != p) return;
                    string code = "";
                    try { code = $" с кодом {p.ExitCode}"; ExitInfo = $"exit {p.ExitCode}"; } catch { ExitInfo = "exit"; }
                    State = ServerState.Crashed;
                    TryDelete(Paths.State);
                    Emit($"Сервер неожиданно завершился{code}. Последние строки журнала — выше.", LogKind.Error);
                    Exited?.Invoke();
                };
            }
            catch { }
        }

        static readonly Regex rxPrompt = new(@"prompt eval time\s*=.*?([\d.]+) tokens per second", RegexOptions.Compiled);
        static readonly Regex rxGen = new(@"(?<!prompt )\beval time\s*=.*?([\d.]+) tokens per second", RegexOptions.Compiled);
        static readonly Regex rxDraft = new(@"acceptance(?: rate)?\s*[=:]\s*([\d.]+)",RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex rxStamp = new(@"^(\d+)\.(\d{2})\.(\d{3})\.\d{3} ", RegexOptions.Compiled);
        static readonly Regex rxGenTokens = new(@"(?<!prompt )\beval time\s*=\s*[\d.]+ ms /\s*(\d+) tokens", RegexOptions.Compiled);
        static readonly Regex rxImportant = new(@"eval time|acceptance|listening|model loaded|all slots are idle|error|warn|fail|exception|abort|out of memory",
                                                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        void StartTail(bool fromEnd)
        {
            tail?.Cancel();
            var cts = tail = new CancellationTokenSource();
            var file = LogFile;
            Task.Run(async () =>
            {
                long pos = -1;
                var pending = new StringBuilder();
                var buf = new byte[1 << 16];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            if (pos < 0) pos = fromEnd ? Math.Max(0, fs.Length - 48 * 1024) : 0;
                            if (fs.Length < pos) pos = 0;
                            fs.Position = pos;
                            int n;
                            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                            {
                                pos += n;
                                pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                                var text = pending.ToString();
                                int last = text.LastIndexOf('\n');
                                if (last < 0) continue;
                                pending.Clear().Append(text[(last + 1)..]);
                                foreach (var l in text[..last].Split('\n')) Parse(l.TrimEnd('\r'));
                            }
                        }
                    }
                    catch (IOException) { }
                    await Task.Delay(200, cts.Token).ContinueWith(_ => { });
                }
            });
        }

        void Parse(string l)
        {
            if (l.Length == 0) return;
            Match m;
            // llama.cpp stamps lines with minutes.seconds.ms.us since the process started
            var at = DateTime.Now;
            if ((m = rxStamp.Match(l)).Success && Since != default)
                at = Since + new TimeSpan(0, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            bool live = at >= countFrom;
            if ((m = rxGenTokens.Match(l)).Success) lastGenTokens = int.Parse(m.Groups[1].Value);
            if (l.Contains("stop processing"))
            {
                if (live) { LastDone = at; Completed?.Invoke(at, lastGenTokens); }
                lastGenTokens = 0;
            }
            if (live && l.Contains("unauthorized")) Rejected?.Invoke(at);
            if ((m = rxPrompt.Match(l)).Success) PromptTps = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if ((m = rxGen.Match(l)).Success) GenTps = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if ((m = rxDraft.Match(l)).Success) DraftAccept = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            var low = l.ToLowerInvariant();
            var kind = LogKind.Server;
            if (low.Contains("illegal memory access") || low.Contains("cuda error"))
            {
                kind = LogKind.Error;
                if (!stopping) CudaError?.Invoke();
            }
            else if (low.Contains("error") || low.Contains("failed") || low.Contains("exception") || low.Contains("out of memory")) kind = LogKind.Error;
            else if (low.Contains("warn")) kind = LogKind.Warn;
            else if (low.Contains("server is listening") || low.Contains("model loaded")) kind = LogKind.Good;
            Line?.Invoke(new LogLine { Time = at.ToString("HH:mm:ss"), Text = l, Kind = kind, Important = kind != LogKind.Server || rxImportant.IsMatch(l), FromServer = true });
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch { } }
    }
}
