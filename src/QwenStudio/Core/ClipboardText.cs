using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace QwenStudio.Core
{
    /// <summary>
    /// Plain-text copy through the Win32 clipboard. WPF's Clipboard.SetDataObject(text, true) calls OleFlushClipboard
    /// after setting the data; clipboard listeners (Parsec sync, PowerToys) open the clipboard right then, so the flush
    /// spends ~1 s in WPF's internal retries and throws CLIPBRD_E_CANT_OPEN — the UI froze for seconds on every copy.
    /// Here the text is rendered immediately (CF_UNICODETEXT), so nothing needs flushing; measured 0 ms under Parsec.
    /// </summary>
    public static class ClipboardText
    {
        const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

        /// <summary>
        /// Returns false if another app kept the clipboard open for the whole ~200 ms of retries.
        /// secret: the text is kept out of Windows clipboard history and cloud clipboard (an API key).
        /// </summary>
        public static bool Set(string text, bool secret = false)
        {
            for (int i = 0; i < 20; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        var chars = (text + "\0").ToCharArray();
                        if (!Put(CF_UNICODETEXT, chars, chars.Length * 2)) return false;
                        if (secret)
                        {
                            // documented opt-outs read by clipboard history, cloud clipboard and clipboard monitors
                            Put(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), null, 4);
                            Put(RegisterClipboardFormat("CanIncludeInClipboardHistory"), null, 4);
                            Put(RegisterClipboardFormat("CanUploadToCloudClipboard"), null, 4);
                        }
                        return true;
                    }
                    finally { CloseClipboard(); }
                }
                Thread.Sleep(10);
            }
            return false;
        }

        /// <summary>Puts one format on the open clipboard: the characters, or a zero DWORD when chars is null.</summary>
        static bool Put(uint format, char[] chars, int bytes)
        {
            if (format == 0) return false;
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) return false;
            var p = GlobalLock(mem);
            if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
            if (chars != null) Marshal.Copy(chars, 0, p, chars.Length); else Marshal.WriteInt32(p, 0);
            GlobalUnlock(mem);
            if (SetClipboardData(format, mem) != IntPtr.Zero) return true;     // the system owns mem now
            GlobalFree(mem);
            return false;
        }
    }
}
