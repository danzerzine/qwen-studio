using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace QwenStudio.Core
{
    /// <summary>
    /// Image generation through ComfyUI on :8188. COMFYUI_DIR in server_config.env points at the ComfyUI folder
    /// (the portable build's root, the ComfyUI folder itself, or any file inside it such as run_nvidia_gpu.bat).
    /// Studio starts ComfyUI with its own Python and stops it by the port, so it never touches other Python processes.
    /// </summary>
    public sealed class Images
    {
        public const int ComfyPort = 8188;

        /// <summary>Card memory above which ComfyUI counts as holding its models (idle ComfyUI + desktop ≈ 0.5–0.7 GB).</summary>
        public const int HeldThresholdMiB = 2048;

        readonly Settings cfg;
        readonly Gpu gpu;
        public Images(Settings cfg, Gpu gpu) { this.cfg = cfg; this.gpu = gpu; }

        public string Url => $"http://127.0.0.1:{ComfyPort}";
        public string LogFile => Path.Combine(Paths.Logs, "comfyui.log");

        /// <summary>(python.exe, main.py) of the configured ComfyUI, or nulls if it is not found.</summary>
        public (string python, string main) Find()
        {
            var p = Paths.Resolve(cfg.Env.Get("COMFYUI_DIR"));
            if (p == "") return (null, null);
            var dir = File.Exists(p) ? Path.GetDirectoryName(p) : p;
            if (!Directory.Exists(dir)) return (null, null);
            // portable build: <root>\python_embeded + <root>\ComfyUI\main.py; git install: <ComfyUI>\main.py + venv
            foreach (var root in new[] { dir, Path.GetDirectoryName(dir) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var main = new[] { Path.Combine(root, "ComfyUI", "main.py"), Path.Combine(root, "main.py") }.FirstOrDefault(File.Exists);
                if (main == null) continue;
                var mainDir = Path.GetDirectoryName(main);
                var py = new[]
                {
                    Path.Combine(root, "python_embeded", "python.exe"),
                    Path.Combine(mainDir, ".venv", "Scripts", "python.exe"),
                    Path.Combine(mainDir, "venv", "Scripts", "python.exe"),
                }.FirstOrDefault(File.Exists) ?? "python.exe";
                return (py, main);
            }
            return (null, null);
        }

        public bool Installed => Find().main != null;
        public string OutputDir { get { var m = Find().main; return m == null ? null : Path.Combine(Path.GetDirectoryName(m), "output"); } }

        public bool ComfyUp { get; private set; }
        /// <summary>
        /// Card memory in use while ComfyUI is up (NVML, whole card). Meaningful only while the LLM server is stopped:
        /// then it is ComfyUI's models. ComfyUI's own torch_vram_* fields can miss them,
        /// and on WDDM per-process usage is not reported.
        /// </summary>
        public int HeldMiB { get; private set; }
        public bool Generating { get; private set; }

        public async Task Probe()
        {
            var stats = await Http.Get($"{Url}/system_stats");
            ComfyUp = stats != null;
            HeldMiB = 0;
            if (ComfyUp) { var g = await Task.Run(gpu.Read); if (g.Ok) HeldMiB = (int)g.UsedMiB; }
            Generating = false;
            if (ComfyUp)
            {
                var q = await Http.Get($"{Url}/queue");
                try { using var d = JsonDocument.Parse(q ?? "{}"); Generating = d.RootElement.GetProperty("queue_running").GetArrayLength() > 0; } catch { }
            }
        }

        /// <summary>Starts ComfyUI and waits until it answers (up to 3 minutes).</summary>
        public async Task<(bool ok, string message)> Start()
        {
            var (py, main) = Find();
            if (main == null) return (false, L.T("Не найден ComfyUI: укажите папку в «Настройки → Сервер и модели» (COMFYUI_DIR)."));
            var extra = cfg.Env.Get("COMFYUI_ARGS");
            // cmd is used only for the "> file" redirect, so ComfyUI keeps logging after Studio closes
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(main),
                Arguments = $"/d /c \"\"{py}\" -s \"{main}\" --windows-standalone-build --port {ComfyPort} {extra} > \"{LogFile}\" 2>&1\"",
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            try { Process.Start(psi); }
            catch (Exception e) { return (false, e.Message); }
            for (int i = 0; i < 180; i++)
            {
                await Task.Delay(1000);
                await Probe();
                if (ComfyUp) return (true, null);
            }
            return (false, L.F("ComfyUI не ответил за 3 минуты — журнал: {0}", LogFile));
        }

        /// <summary>Stops the process listening on ComfyUI's port — only if it is a Python (ComfyUI) process.</summary>
        public Task Stop() => Task.Run(async () =>
        {
            int pid = Net.PortOwner(ComfyPort);
            if (pid == 0) return;
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return;
                p.Kill(true);
                p.WaitForExit(10000);
            }
            catch { }
            await Probe();
        });

        /// <summary>Asks ComfyUI to drop its models from VRAM; it keeps running and reloads them on the next picture.</summary>
        public async Task FreeVram()
        {
            await Http.Post($"{Url}/free", "{\"unload_models\":true,\"free_memory\":true}");
            for (int i = 0; i < 20; i++) { await Task.Delay(500); await Probe(); if (HeldMiB < HeldThresholdMiB) break; }
        }
    }
}
