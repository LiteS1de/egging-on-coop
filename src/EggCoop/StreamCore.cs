using System;
using System.Collections.Generic;

namespace EggCoop
{
    // Viewer effects queue (Unity-free, checked by src/SelfTest). Viewers pay (channel points, bits) for an effect;
    // it runs when the game allows it (in play, not cracked, not paused), at most one effect per Gap seconds and
    // each effect no more often than its own cooldown. A request that cannot run within MaxWait comes back as
    // expired, so the caller can refund it.
    public class StreamQueue
    {
        public class Req
        {
            public string Effect, Viewer, Source;   // source: "points", "bits", "test"
            public object Tag;                      // e.g. the Twitch redemption to fulfill or refund
            public float At;
        }

        public float Gap = 12f, MaxWait = 60f;
        public Func<string, float> Cooldown = _ => 0f;
        readonly List<Req> queue = new List<Req>();
        readonly Dictionary<string, float> lastRun = new Dictionary<string, float>();
        float lastAny = float.MinValue;

        public int Count => queue.Count;
        public IEnumerable<Req> Pending => queue;

        public void Add(Req r) => queue.Add(r);

        public void Clear(List<Req> dropped) { dropped?.AddRange(queue); queue.Clear(); }

        // The next request that may run now (oldest first), or null. Requests waiting too long go to `expired`.
        public Req Next(float now, Func<string, bool> canRun, List<Req> expired)
        {
            for (int i = queue.Count - 1; i >= 0; i--)
                if (now - queue[i].At > MaxWait) { expired?.Add(queue[i]); queue.RemoveAt(i); }
            if (now - lastAny < Gap) return null;
            for (int i = 0; i < queue.Count; i++)
            {
                var r = queue[i];
                if (lastRun.TryGetValue(r.Effect, out var t) && now - t < Cooldown(r.Effect)) continue;
                if (!canRun(r.Effect)) continue;
                queue.RemoveAt(i);
                lastRun[r.Effect] = now; lastAny = now;
                return r;
            }
            return null;
        }

        public float CooldownLeft(string effect, float now) =>
            lastRun.TryGetValue(effect, out var t) ? Math.Max(0f, Cooldown(effect) - (now - t)) : 0f;
    }
}
