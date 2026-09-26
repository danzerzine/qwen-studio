using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QwenStudio.Core
{
    /// <summary>Reads what a model can do from its GGUF header (no tensors are touched).</summary>
    public static class Gguf
    {
        static readonly Dictionary<string, (DateTime at, long size, bool? think)> cache = new();

        /// <summary>
        /// The model's chat template knows reasoning (Qwen's enable_thinking, a &lt;think&gt; block).
        /// Null when the file is missing, unreadable or has no template — the caller should not block on a guess.
        /// </summary>
        public static bool? Thinks(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var fi = new FileInfo(path);
                lock (cache)
                    if (cache.TryGetValue(path, out var c) && c.at == fi.LastWriteTimeUtc && c.size == fi.Length) return c.think;
                var t = ChatTemplate(path);
                bool? think = t == null ? null : t.Contains("enable_thinking") || t.Contains("<think>");
                lock (cache) cache[path] = (fi.LastWriteTimeUtc, fi.Length, think);
                return think;
            }
            catch { return null; }
        }

        /// <summary>tokenizer.chat_template from the header, or null.</summary>
        public static string ChatTemplate(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != 0x46554747) return null;        // "GGUF"
            if (r.ReadUInt32() < 2) return null;                  // v1 used 32-bit counts
            r.ReadUInt64();                                       // tensors
            ulong kvs = r.ReadUInt64();
            for (ulong i = 0; i < kvs; i++)
            {
                var key = Str(r);
                uint type = r.ReadUInt32();
                if (key == "tokenizer.chat_template" && type == 8) return Str(r);
                Skip(r, type);
            }
            return null;
        }

        static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(checked((int)r.ReadUInt64())));

        static void Skip(BinaryReader r, uint type)
        {
            switch (type)
            {
                case 8: r.BaseStream.Seek(checked((long)r.ReadUInt64()), SeekOrigin.Current); break;
                case 9:
                    uint et = r.ReadUInt32();
                    ulong n = r.ReadUInt64();
                    int size = Size(et);
                    if (size > 0) r.BaseStream.Seek(checked((long)n * size), SeekOrigin.Current);
                    else for (ulong j = 0; j < n; j++) Skip(r, et);
                    break;
                default:
                    int s = Size(type);
                    if (s <= 0) throw new InvalidDataException("GGUF: unknown value type " + type);
                    r.BaseStream.Seek(s, SeekOrigin.Current);
                    break;
            }
        }

        /// <summary>Fixed size of a scalar GGUF type; 0 for strings and arrays.</summary>
        static int Size(uint type) => type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0,
        };
    }
}
