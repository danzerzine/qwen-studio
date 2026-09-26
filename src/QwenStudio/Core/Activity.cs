using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace QwenStudio.Core
{
    /// <summary>
    /// What the server is doing right now, from GET /slots: how many slots are busy and how fast
    /// all of them generate together. The live rate is the sum of per-slot n_decoded growth over the last few seconds.
    /// </summary>
    public sealed class Activity
    {
        public bool Ok { get; private set; }
        public int Slots { get; private set; }
        public int Busy { get; private set; }
        /// <summary>Last moment a slot was seen busy.</summary>
        public DateTime? LastBusy { get; private set; }
        /// <summary>Tokens per second summed over all slots, averaged over the window; null when nothing generates.</summary>
        public double? AggTps { get; private set; }

        static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
        readonly Dictionary<int, (int task, int decoded)> prev = new();
        readonly Queue<(DateTime at, int tokens)> samples = new();

        public void Reset()
        {
            Ok = false; Slots = Busy = 0; AggTps = null;
            prev.Clear(); samples.Clear();
        }

        public async Task Poll(int port, string key)
        {
            var json = await Http.GetAuth($"http://127.0.0.1:{port}/slots", key);
            var now = DateTime.Now;
            if (json == null) { Ok = false; return; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                int slots = 0, busy = 0, tokens = 0;
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    slots++;
                    int id = s.GetProperty("id").GetInt32();
                    bool proc = s.TryGetProperty("is_processing", out var p) && p.GetBoolean();
                    int task = s.TryGetProperty("id_task", out var t) ? t.GetInt32() : -1;
                    int dec = s.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0
                              && nt[0].TryGetProperty("n_decoded", out var d) ? d.GetInt32() : 0;
                    if (proc)
                    {
                        busy++;
                        if (prev.TryGetValue(id, out var was) && was.task == task) tokens += Math.Max(0, dec - was.decoded);
                        else tokens += dec;                          // a request that started since the last poll
                    }
                    prev[id] = (task, proc ? dec : 0);
                }
                Ok = true; Slots = slots; Busy = busy;
                if (busy > 0) LastBusy = now;
                samples.Enqueue((now, tokens));
                while (samples.Count > 0 && now - samples.Peek().at > Window) samples.Dequeue();
                // each sample holds the tokens made since the previous one, so the oldest sample's tokens lie before the span
                int sum = samples.Count > 1 ? samples.Skip(1).Sum(x => x.tokens) : tokens;
                double span = samples.Count > 1 ? (now - samples.Peek().at).TotalSeconds : 1;
                // a request that just finished still shows in the window; hide the figure once all slots are idle
                AggTps = busy > 0 && sum > 0 ? sum / Math.Max(1, span) : null;
            }
            catch { Ok = false; }
        }
    }
}
