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
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

        /// <summary>Returns false if another app kept the clipboard open for the whole ~200 ms of retries.</summary>
        public static bool Set(string text)
        {
            for (int i = 0; i < 20; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                        if (mem == IntPtr.Zero) return false;
                        var p = GlobalLock(mem);
                        Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                        Marshal.WriteInt16(p, text.Length * 2, 0);
                        GlobalUnlock(mem);
                        if (SetClipboardData(CF_UNICODETEXT, mem) != IntPtr.Zero) return true; // the system owns mem now
                        GlobalFree(mem);
                        return false;
                    }
                    finally { CloseClipboard(); }
                }
                Thread.Sleep(10);
            }
            return false;
        }
    }
}
