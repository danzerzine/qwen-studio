using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QwenStudio.Core
{
    /// <summary>One minute of server life in logs\studio\stats.jsonl (only minutes when the server was up).</summary>
    public sealed class MinuteStat
    {
        string ts;
        DateTime? at;
        [JsonPropertyName("ts")] public string Ts { get => ts; set { ts = value; at = null; } }
        [JsonPropertyName("up")] public bool Up { get; set; }
        [JsonPropertyName("profile")] public string Profile { get; set; }
        [JsonPropertyName("old")] public bool Old { get; set; }
        /// <summary>"uncensored" for the third model; absent for the main and old ones (they have "old").</summary>
        [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Model { get; set; }
        [JsonPropertyName("req")] public int Req { get; set; }
        [JsonPropertyName("gen")] public long Gen { get; set; }
        [JsonPropertyName("busy")] public int Busy { get; set; }
        [JsonPropertyName("slots")] public int Slots { get; set; }
        [JsonPropertyName("vram")] public int Vram { get; set; }
        [JsonPropertyName("rej")] public int Rej { get; set; }

        [JsonIgnore] public DateTime At => at ??= DateTime.ParseExact(Ts, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A server lifecycle event in logs\studio\sessions.jsonl — the crash ledger that failure_report.py reads.</summary>
    public sealed class SessionEvent
    {
        string ts;
        DateTime? at;
        [JsonPropertyName("ts")] public string Ts { get => ts; set { ts = value; at = null; } }
        [JsonPropertyName("event")] public string Event { get; set; }
        [JsonPropertyName("profile")] public string Profile { get; set; }
        [JsonPropertyName("old")] public bool Old { get; set; }
        /// <summary>"uncensored" for the third model; absent for the main and old ones (they have "old").</summary>
        [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Model { get; set; }
        [JsonPropertyName("build")] public string Build { get; set; }
        [JsonPropertyName("driver")] public string Driver { get; set; }
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("uptime_min")] public double UptimeMin { get; set; }
        [JsonPropertyName("requests")] public int Requests { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; }

        [JsonIgnore] public DateTime At => at ??= DateTime.ParseExact(Ts, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        [JsonIgnore] public bool IsFailure => Event is "crash" or "cuda";
    }

    public sealed class Bucket
    {
        public DateTime Start;
        public int Req, UpMinutes, BusyMax, Slots, Rej;
        public long Gen;
        public List<SessionEvent> Failures = new();
    }

    /// <summary>Totals for a period (a day, a week, all time), optionally for one mode.</summary>
    public sealed class Totals
    {
        public int Req, UpMinutes, Rej, Failures, BusyMax;
        public long Gen;
        public void Add(MinuteStat m)
        {
            Req += m.Req; Gen += m.Gen; Rej += m.Rej;
            if (m.Up) UpMinutes++;
            BusyMax = Math.Max(BusyMax, m.Busy);
        }
        public void Add(Totals t)
        {
            Req += t.Req; Gen += t.Gen; Rej += t.Rej; UpMinutes += t.UpMinutes; Failures += t.Failures;
            BusyMax = Math.Max(BusyMax, t.BusyMax);
        }
    }

    /// <summary>
    /// Uptime and load history. Minutes are accumulated in memory and appended to stats.jsonl once they are over;
    /// lifecycle events go to sessions.jsonl right away.
    /// </summary>
    public sealed class Ledger
    {
        public static string StatsFile => Path.Combine(Paths.Logs, "stats.jsonl");
        public static string SessionsFile => Path.Combine(Paths.Logs, "sessions.jsonl");

        static readonly JsonSerializerOptions json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>Written minutes of the last 25 hours plus the open ones.</summary>
        readonly List<MinuteStat> recent = new();
        readonly SortedDictionary<DateTime, MinuteStat> open = new();
        public List<SessionEvent> Events { get; } = new();
        /// <summary>All history folded by (day, mode): the statistics tab's source. Mode is "agent", "chat|old" etc.</summary>
        readonly Dictionary<(DateTime day, string mode), Totals> days = new();
        /// <summary>First day with records.</summary>
        public DateTime? FirstDay => days.Count > 0 ? days.Keys.Min(k => k.day) : null;

        /// <summary>Since the last crash (or since records began): up minutes and requests.</summary>
        public DateTime? CleanSince { get; private set; }
        public int CleanMinutes { get; private set; }
        public long CleanRequests { get; private set; }
        public bool HasFailures => Events.Any(e => e.IsFailure);

        DateTime written;       // minutes before this are already in the file

        static DateTime Minute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);

        /// <summary>Reads the files and fills gaps from the server logs. Call once, off the UI thread.</summary>
        public void Load(string fallbackModelName, string uncensoredModelName, string liveLog)
        {
            foreach (var e in ReadJsonl<SessionEvent>(SessionsFile)) Events.Add(e);
            var all = ReadJsonl<MinuteStat>(StatsFile);
            var now = DateTime.Now;
            var from = all.Count > 0 ? all[^1].At.AddMinutes(1) : now.AddDays(-7);
            var filled = Backfill(from, Minute(now), fallbackModelName, uncensoredModelName, liveLog);
            if (filled.Count > 0)
            {
                Append(StatsFile, filled);
                all.AddRange(filled);
            }
            written = Minute(now);
            recent.AddRange(all.Where(m => m.At >= now.AddHours(-25)));
            foreach (var m in all) Fold(m);

            var lastFail = Events.LastOrDefault(e => e.IsFailure);
            CleanSince = lastFail?.At ?? (all.Count > 0 ? all[0].At : null);
            foreach (var m in all.Where(m => lastFail == null || m.At >= lastFail.At))
            {
                if (m.Up) CleanMinutes++;
                CleanRequests += m.Req;
            }
        }

        MinuteStat Open(DateTime at)
        {
            var k = Minute(at);
            if (k < written) k = written;           // late news about a minute already written goes to the current one
            if (!open.TryGetValue(k, out var m)) open[k] = m = new MinuteStat { Ts = k.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
            return m;
        }

        /// <summary>Once a second: what the server is doing now. Writes out the minutes that are over.</summary>
        public void Tick(DateTime now, bool up, Profile p, int busy, int slots, int vramMiB)
        {
            var m = Open(now);
            if (up)
            {
                m.Up = true;
                if (p != null) { m.Profile = p.Id; m.Old = p.Old; m.Model = ExtraSlot(p); }
                m.Busy = Math.Max(m.Busy, busy);
                m.Slots = Math.Max(m.Slots, slots);
                m.Vram = Math.Max(m.Vram, vramMiB);
            }
            var cur = Minute(now);
            var done = open.Where(kv => kv.Key < cur).Select(kv => kv.Value).ToList();
            if (done.Count == 0) return;
            foreach (var d in done) open.Remove(d.At);
            var keep = done.Where(d => d.Up || d.Req > 0 || d.Rej > 0).ToList();
            if (keep.Count > 0) Append(StatsFile, keep);
            foreach (var d in keep)
            {
                recent.Add(d);
                Fold(d);
                if (d.Up) CleanMinutes++;
                CleanRequests += d.Req;
                CleanSince ??= d.At;
            }
            written = cur;
            recent.RemoveAll(r => r.At < now.AddHours(-25));
        }

        /// <summary>On exit: write the minute in progress too; the next session continues after it.</summary>
        public void Flush()
        {
            var keep = open.Values.Where(d => d.Up || d.Req > 0 || d.Rej > 0).ToList();
            open.Clear();
            if (keep.Count > 0) Append(StatsFile, keep);
        }

        public void Request(DateTime at, int genTokens) { var m = Open(at); m.Req++; m.Gen += genTokens; }
        public void Reject(DateTime at) => Open(at).Rej++;

        public void Event(string ev, Profile p = null, string build = null, string driver = null, int pid = 0,
                          double uptimeMin = 0, int requests = 0, string reason = null)
        {
            var e = new SessionEvent
            {
                Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Event = ev,
                Profile = p?.Id, Old = p?.Old ?? false, Model = p == null ? null : ExtraSlot(p), Build = build, Driver = driver, Pid = pid,
                UptimeMin = Math.Round(uptimeMin, 1), Requests = requests, Reason = reason,
            };
            Events.Add(e);
            Append(SessionsFile, new[] { e });
            if (e.IsFailure) { CleanSince = e.At; CleanMinutes = 0; CleanRequests = 0; }
        }

        IEnumerable<MinuteStat> Minutes => recent.Concat(open.Values);

        /// <summary>Toggles are not modes: minutes of the former «Без размышлений» and «Зрение» cards count as «Чат».</summary>
        public static string ModeOf(MinuteStat m) => (Profile.BaseId(m.Profile) ?? "?") + (m.Old ? "|old" : m.Model == Profile.UncensoredSlot ? "|uncensored" : "");

        /// <summary>What goes into "model": only the slot the "old" flag cannot say.</summary>
        static string ExtraSlot(Profile p) => p.Slot == Profile.UncensoredSlot ? p.Slot : null;

        void Fold(MinuteStat m)
        {
            var k = (m.At.Date, ModeOf(m));
            if (!days.TryGetValue(k, out var t)) days[k] = t = new Totals();
            t.Add(m);
        }

        /// <summary>Totals for days in [from, to), including the minute in progress; failures come from the event ledger.</summary>
        public Totals Period(DateTime? from, DateTime? to = null, string mode = null)
        {
            var res = new Totals();
            bool In(DateTime d) => (from == null || d >= from) && (to == null || d < to);
            foreach (var kv in days)
                if (In(kv.Key.day) && (mode == null || kv.Key.mode == mode)) res.Add(kv.Value);
            foreach (var m in open.Values)
                if (In(m.At.Date) && (mode == null || ModeOf(m) == mode)) res.Add(m);
            if (mode == null) res.Failures = Events.Count(e => e.IsFailure && In(e.At.Date));
            return res;
        }

        /// <summary>Per-day totals for the n days that end today.</summary>
        public List<(DateTime day, Totals t)> Days(DateTime today, int n) =>
            Enumerable.Range(0, n).Select(i => today.Date.AddDays(i - n + 1)).Select(d => (d, Period(d, d.AddDays(1)))).ToList();

        /// <summary>Modes that ever ran, busiest first.</summary>
        public List<string> Modes() => days.Keys.Select(k => k.mode).Concat(open.Values.Where(m => m.Up || m.Req > 0).Select(ModeOf))
            .Distinct().OrderByDescending(m => Period(null, null, m).Req).ToList();

        public int RequestsSince(DateTime t) => Minutes.Where(m => m.At >= Minute(t)).Sum(m => m.Req);
        public int RejectedSince(DateTime t) => Minutes.Where(m => m.At >= Minute(t)).Sum(m => m.Rej);

        /// <summary>n buckets of the given size ending with the one that holds "now".</summary>
        public List<Bucket> Buckets(DateTime now, int n, TimeSpan size)
        {
            long ticks = size.Ticks;
            var lastStart = new DateTime(now.Ticks / ticks * ticks, now.Kind);
            var first = lastStart - TimeSpan.FromTicks(ticks * (n - 1));
            var res = Enumerable.Range(0, n).Select(i => new Bucket { Start = first + TimeSpan.FromTicks(ticks * i) }).ToList();
            foreach (var m in Minutes)
            {
                int i = (int)((m.At - first).Ticks / ticks);
                if (m.At < first || i >= n) continue;
                var b = res[i];
                b.Req += m.Req; b.Gen += m.Gen; b.Rej += m.Rej;
                if (m.Up) b.UpMinutes++;
                b.BusyMax = Math.Max(b.BusyMax, m.Busy);
                b.Slots = Math.Max(b.Slots, m.Slots);
            }
            foreach (var e in Events.Where(e => e.IsFailure))
            {
                int i = (int)((e.At - first).Ticks / ticks);
                if (e.At >= first && i < n) res[i].Failures.Add(e);
            }
            return res;
        }

        // ───────────────────────── backfill ─────────────────────────

        static readonly Regex rxStamp = new(@"^(\d+)\.(\d{2})\.(\d{3})\.\d{3} ", RegexOptions.Compiled);
        static readonly Regex rxGen = new(@"(?<!prompt )\beval time\s*=\s*[\d.]+ ms /\s*(\d+) tokens", RegexOptions.Compiled);
        static readonly Regex rxName = new(@"^server-(.+)-(\d{8}-\d{6})\.log$", RegexOptions.Compiled);

        /// <summary>
        /// Rebuilds minutes in [from, to) from Studio's server logs: what the server served and when it was up.
        /// A finished log counts as up from its first to its last line (idle time after that is unknown).
        /// </summary>
        static List<MinuteStat> Backfill(DateTime from, DateTime to, string fallbackModelName, string uncensoredModelName, string liveLog)
        {
            var minutes = new SortedDictionary<DateTime, MinuteStat>();
            if (from >= to) return new();
            foreach (var f in Directory.GetFiles(Paths.Logs, "server-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < from) continue;
                    var nm = rxName.Match(Path.GetFileName(f));
                    if (!nm.Success) continue;
                    var start = DateTime.ParseExact(nm.Groups[2].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    bool old = false;
                    string model = null;
                    int gen = 0;
                    DateTime? prev = null;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var rd = new StreamReader(fs, Encoding.UTF8);
                    string l;
                    while ((l = rd.ReadLine()) != null)
                    {
                        var m = rxStamp.Match(l);
                        if (!m.Success) continue;
                        var at = start + new TimeSpan(0, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                        if (!string.IsNullOrEmpty(fallbackModelName) && l.Contains(fallbackModelName)) old = true;
                        if (!string.IsNullOrEmpty(uncensoredModelName) && l.Contains(uncensoredModelName)) model = Profile.UncensoredSlot;
                        Match g;
                        if ((g = rxGen.Match(l)).Success) gen = int.Parse(g.Groups[1].Value);
                        // every minute between two log lines was up — the process was alive to write the second one
                        for (var t = Minute(prev ?? at); t <= Minute(at); t = t.AddMinutes(1))
                            if (t >= from && t < to) Get(t).Up = true;
                        prev = at;
                        if (at < from || at >= to) continue;
                        if (l.Contains("stop processing")) { var s = Get(Minute(at)); s.Req++; s.Gen += gen; gen = 0; }
                        else if (l.Contains("unauthorized")) Get(Minute(at)).Rej++;
                    }
                    // the server of this session is still alive: idle minutes after its last line were up too
                    if (liveLog != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(liveLog), StringComparison.OrdinalIgnoreCase))
                        for (var t = Minute(prev ?? start); t < to; t = t.AddMinutes(1))
                            if (t >= from) Get(t).Up = true;

                    MinuteStat Get(DateTime k)
                    {
                        if (!minutes.TryGetValue(k, out var s))
                            minutes[k] = s = new MinuteStat { Ts = k.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Profile = nm.Groups[1].Value, Old = old, Model = model };
                        return s;
                    }
                }
                catch (IOException) { }
            }
            return minutes.Values.ToList();
        }

        // ───────────────────────── files ─────────────────────────

        static List<T> ReadJsonl<T>(string path)
        {
            var res = new List<T>();
            if (!File.Exists(path)) return res;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { var v = JsonSerializer.Deserialize<T>(line.TrimStart('﻿')); if (v != null) res.Add(v); } catch { }
            }
            return res;
        }

        static void Append<T>(string path, IEnumerable<T> items)
        {
            var sb = new StringBuilder();
            foreach (var i in items) sb.Append(JsonSerializer.Serialize(i, json)).Append('\n');
            try { File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
    }
}
