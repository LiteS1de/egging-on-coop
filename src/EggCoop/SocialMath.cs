using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EggCoop
{
    // Unity-free parts of emotes and the shame board, so src/SelfTest can check them.
    public static class SocialMath
    {
        // Which slice of a wheel with `count` slices the mouse is over: -1 = the centre (the close button),
        // else 0..count-1 clockwise from the top. dx/dy in screen pixels from the centre, y growing downward.
        // Past the rim still counts: a quick flick toward an icon picks it.
        public static int Sector(float dx, float dy, float inner, int count)
        {
            if (dx * dx + dy * dy < inner * inner) return -1;
            double a = Math.Atan2(dx, -dy) * 180 / Math.PI;
            if (a < 0) a += 360;
            return (int)((a + 180.0 / count) / (360.0 / count)) % count;
        }
    }

    // Height lost between two moments of rest: a fall is counted when the egg stops after dropping MinDrop or more.
    public class FallTracker
    {
        public const float MinDrop = 4f, RestSpeed = 0.5f, RestTime = 0.6f;
        float peak = float.NaN, restFor;

        public void Reset(float y) { peak = y; restFor = 0f; }

        // Feed every frame; returns the drop (m) on the frame the egg comes to rest after a fall, else 0.
        public float Step(float y, float speed, float dt)
        {
            if (float.IsNaN(peak) || y > peak) peak = y;
            restFor = speed < RestSpeed ? restFor + dt : 0f;
            if (restFor < RestTime) return 0f;
            float drop = peak - y;
            peak = y;
            return drop >= MinDrop ? drop : 0f;
        }
    }

    public class Shame
    {
        public int Cracks, Falls;
        public float MaxFall, Lost;

        public void AddFall(float drop) { Falls++; Lost += drop; MaxFall = Math.Max(MaxFall, drop); }
        public bool Same(Shame o) => o != null && o.Cracks == Cracks && o.Falls == Falls && o.MaxFall == MaxFall && o.Lost == Lost;

        // Config line "nick:cracks,falls,maxFall,lost;..." for every player we have seen.
        // shortcut: a nick containing ';' breaks its own entry; nicks are short free text, fine until someone complains.
        public static string Format(Dictionary<string, Shame> all) => string.Join(";", all.Select(kv =>
            kv.Key + ":" + string.Join(",", kv.Value.Cracks, kv.Value.Falls, kv.Value.MaxFall.ToString("0.#", CultureInfo.InvariantCulture), kv.Value.Lost.ToString("0.#", CultureInfo.InvariantCulture))));

        public static Dictionary<string, Shame> Parse(string text)
        {
            var all = new Dictionary<string, Shame>();
            foreach (var part in (text ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.LastIndexOf(':');
                var v = c > 0 ? part.Substring(c + 1).Split(',') : new string[0];
                if (v.Length == 4 && int.TryParse(v[0], out var cr) && int.TryParse(v[1], out var fa)
                    && float.TryParse(v[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mx) && float.TryParse(v[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
                    all[part.Substring(0, c)] = new Shame { Cracks = cr, Falls = fa, MaxFall = mx, Lost = lo };
            }
            return all;
        }
    }
}

namespace EggCoop
{
    // Streamer mode text filter (Unity-free for src/SelfTest): IPv4, IPv6 and invite codes become dots.
    public static class StreamerMask
    {
        static readonly System.Text.RegularExpressions.Regex Ipv4 = new System.Text.RegularExpressions.Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        static readonly System.Text.RegularExpressions.Regex Ipv6 = new System.Text.RegularExpressions.Regex(@"\[?(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}\]?");
        static readonly System.Text.RegularExpressions.Regex Code = new System.Text.RegularExpressions.Regex(@"\b[0-9A-Za-z]{4}-[0-9A-Za-z]{4}-[0-9A-Za-z]{4}\b");

        public static string Apply(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Code.Replace(s, "••••-••••-••••");
            s = Ipv4.Replace(s, "•.•.•.•");
            // A clock like 10:41:09 also looks like hex groups: only "::" or 4+ groups count as IPv6.
            return Ipv6.Replace(s, m => m.Value.Contains("::") || m.Value.Split(':').Length > 3 ? "[•:•]" : m.Value);
        }
    }
}
