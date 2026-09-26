using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QwenStudio.Core
{
    /// <summary>Start Studio with Windows: a value under HKCU\...\Run (per user, no admin rights).</summary>
    public static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "QwenStudio";
        public const string Flag = "--autostart";

        static string Command => $"\"{Environment.ProcessPath}\" {Flag}";

        public static bool Enabled
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string v && v.Length > 0;
            }
        }

        /// <summary>The registered command points at another exe (the app was moved or a test copy registered itself).</summary>
        public static bool Stale
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(Name) is string v && v.Length > 0 && !string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Set(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue(Name, Command);
            else if (k.GetValue(Name) != null) k.DeleteValue(Name);
        }
    }

    /// <summary>
    /// Old server logs go into monthly zips: logs\studio\archive\server-logs-yyyy-MM.zip.
    /// The original goes to the Recycle Bin only after its copy in the zip was read back with the same length.
    /// </summary>
    public static class LogArchive
    {
        public const int KeepDays = 7;
        public static string Dir => Path.Combine(Paths.Logs, "archive");

        public sealed class Result { public int Files; public long Bytes; public List<string> Errors = new(); }

        public static Result Run(string currentLog)
        {
            var res = new Result();
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            var old = Directory.GetFiles(Paths.Logs, "server-*.log")
                .Where(f => !string.Equals(Path.GetFullPath(f), currentLog == null ? null : Path.GetFullPath(currentLog), StringComparison.OrdinalIgnoreCase))
                .Where(f => File.GetLastWriteTime(f) < cutoff)
                .ToList();
            if (old.Count == 0) return res;
            Directory.CreateDirectory(Dir);
            foreach (var f in old)
            {
                try
                {
                    var t = File.GetLastWriteTime(f);
                    var zip = Path.Combine(Dir, $"server-logs-{t:yyyy-MM}.zip");
                    var name = Path.GetFileName(f);
                    long len = new FileInfo(f).Length;
                    using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
                    {
                        z.GetEntry(name)?.Delete();
                        var e = z.CreateEntry(name, CompressionLevel.SmallestSize);
                        e.LastWriteTime = t;
                        using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var dst = e.Open();
                        src.CopyTo(dst);
                    }
                    using (var z = ZipFile.OpenRead(zip))
                    {
                        var e = z.GetEntry(name);
                        long n = 0;
                        if (e != null) { using var s = e.Open(); var buf = new byte[1 << 16]; int r; while ((r = s.Read(buf, 0, buf.Length)) > 0) n += r; }
                        if (n != len) { res.Errors.Add($"{name}: копия в архиве не совпала"); continue; }
                    }
                    if (!Recycle(f)) { res.Errors.Add($"{name}: не удалось убрать в корзину"); continue; }
                    res.Files++; res.Bytes += len;
                }
                catch (Exception ex) { res.Errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
            return res;
        }

        public static (int files, long bytes) Pending(string currentLog)
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            var fs = Directory.GetFiles(Paths.Logs, "server-*.log").Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime < cutoff && !string.Equals(f.FullName, currentLog, StringComparison.OrdinalIgnoreCase)).ToList();
            return (fs.Count, fs.Sum(f => f.Length));
        }

        // ── Recycle Bin via the shell: undoable, silent ──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        public static bool Recycle(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE, pFrom = Path.GetFullPath(path) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
        }
    }
}
