using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using UnityEngine;

using static EggCoop.Lang;

namespace EggCoop
{
    // Emotes over the eggs, ping markers and the shame board. Hold the emote key (G): a wheel opens, hover a slice
    // and let go to send it; the centre "×" (or Esc) closes it. "Метка" then asks for a click on the spot to mark.
    public partial class Plugin
    {
        const byte SEmote = CoopNet.SocialBase, SStats = SEmote + 1, SPing = SEmote + 2, SFall = SEmote + 3, SReset = SEmote + 4;
        const float EmoteTime = 3.5f, PingTime = 12f, BigFall = 15f, EmoteSize = 56f;
        // Twemoji 15.1 (CC-BY 4.0), embedded as emote.<codepoint>.
        static readonly string[] EmoteIcons = { "1f44b", "1f44d", "1f602", "1f631", "1f621", "1f64f", "1f480", null };   // the last slot is the ping: drawn as its triangle
        static string[] EmoteNames => Lang.En ? new[] { "Hi", "Nice!", "Haha", "Aaah!", "Angry", "Help!", "RIP", "Ping" } : new[] { "Привет", "Класс!", "Ха-ха", "Ааа!", "Злюсь", "Помогите!", "RIP", "Метка" };
        const int PingIndex = 7;

        Texture2D[] emoteTex;
        Texture2D wheelDisc, wheelSlice, wheelCenter, wheelCenterHot, triTex;
        GUIStyle sCap, sCloseX, sHint;
        bool wheelOpen, aiming;
        int wheelHover = -1;
        float lastEmoteAt = -10f, nextStats;
        readonly Dictionary<int, (int emote, float t0, string caption)> bubbles = new Dictionary<int, (int, float, string)>();   // player id, -1 = us
        readonly Dictionary<int, (Vector3 at, float t0)> pings = new Dictionary<int, (Vector3, float)>();
        Dictionary<string, Shame> shame = new Dictionary<string, Shame>();
        static readonly FallTracker fall = new FallTracker();
        int lastCracks = -1;

        Shame MyShame => shame.TryGetValue(cfg.Nick.Value, out var s) ? s : shame[cfg.Nick.Value] = new Shame();
        void SaveShame() => cfg.ShameStats.Value = Shame.Format(shame);

        public static string NickOf(int id) => instance != null && instance.ghosts.TryGetValue(id, out var g) ? g.Nick : id == 0 ? T("Хост", "Host") : T("Друг", "Friend");

        // ---------------------------------------------------------------- frame

        void SocialTick(bool inGame)
        {
            float now = Time.unscaledTime;
            if (inGame)
            {
                int cracks = Persistent.instance.saveData.crackCount;
                if (lastCracks >= 0 && cracks > lastCracks) { MyShame.Cracks += cracks - lastCracks; SaveShame(); }
                lastCracks = cracks;
                var egg = World.instance.GetEgg();
                float drop = fall.Step(egg.GetPosition().y, egg.GetVelocity().magnitude, Time.unscaledDeltaTime);
                if (drop > 0) OnFall(drop);
            }
            else { lastCracks = -1; fall.Reset(float.NaN); }
            if (!inGame && (wheelOpen || aiming)) { wheelOpen = aiming = false; UpdateCursor(); }

            // Everyone keeps their own numbers (saved in their config); friends hear them every few seconds.
            if (net.Connected && now >= nextStats)
            {
                nextStats = now + 3f;
                var s = MyShame;
                var w = net.WorldWriter(); w.Put(SStats); w.Put(cfg.Nick.Value, 24); w.Put(s.Cracks); w.Put(s.Falls); w.Put(s.MaxFall); w.Put(s.Lost);
                net.SendWorld(w, DeliveryMethod.ReliableSequenced);
            }
        }

        void OnFall(float drop)
        {
            MyShame.AddFall(drop);
            SaveShame();
            if (drop < BigFall) return;
            Toast(T($"Вы пролетели вниз {drop:0} м", $"You fell {drop:0} m"));
            if (!net.Connected) return;
            var w = net.WorldWriter(); w.Put(SFall); w.Put(drop);
            net.SendWorld(w, DeliveryMethod.ReliableUnordered);
        }

        // true = the key was ours this frame (nothing else may react to it).
        bool HandleSocialKeys()
        {
            if (aiming)
            {
                if (Input.GetMouseButtonDown(0)) PlacePing(Input.mousePosition);
                else if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) { aiming = false; UpdateCursor(); }
                return true;
            }
            if (wheelOpen)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) CloseWheel();
                else if (!Input.GetKey(cfg.EmoteKey.Value)) { if (wheelHover >= 0) FireEmote(wheelHover); else CloseWheel(); }
                return true;
            }
            if (Input.GetKeyDown(cfg.EmoteKey.Value) && InGame()) { wheelOpen = true; wheelHover = -1; UpdateCursor(); return true; }
            // Quick ping: the cursor is locked to the screen centre while playing, i.e. where the camera looks (past our egg).
            if (Input.GetKeyDown(cfg.PingKey.Value) && InGame()) { PlacePing(new Vector2(Screen.width / 2f, Screen.height / 2f)); return true; }
            return false;
        }

        void CloseWheel() { wheelOpen = false; wheelHover = -1; UpdateCursor(); }

        void FireEmote(int i)
        {
            CloseWheel();
            if (i == PingIndex) { aiming = true; UpdateCursor(); return; }
            if (Time.unscaledTime - lastEmoteAt < 0.7f) return;   // no spamming
            lastEmoteAt = Time.unscaledTime;
            bubbles[-1] = (i, Time.unscaledTime, null);
            SendEmote(i, null);
        }

        // caption: a viewer's name under a stream emote (0.5.0 peers ignore the extra bytes).
        void SendEmote(int i, string caption)
        {
            if (!net.Connected) return;
            var w = net.WorldWriter(); w.Put(SEmote); w.Put((byte)i);
            if (caption != null) w.Put(caption, 32);
            net.SendWorld(w, DeliveryMethod.ReliableUnordered);
        }

        void PlacePing(Vector2 screen)
        {
            aiming = false; UpdateCursor();
            var cam = Camera.main;
            if (cam == null || !InGame()) return;
            // First solid thing under the cursor that is not an egg (ours or a friend's). Distant scenery often has no
            // collider: then the ping floats 30 m out along the view, which still shows the direction.
            var ray = cam.ScreenPointToRay(screen);
            var hit = Physics.RaycastAll(ray, 500f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance)
                .FirstOrDefault(h => h.collider.GetComponentInParent<EggCoreData>() == null && h.collider.GetComponentInParent<RawEggController>() == null
                    && !h.collider.transform.root.name.StartsWith("EggCoop_Ghost"));
            var at = hit.collider != null ? hit.point : ray.GetPoint(30f);
            pings[-1] = (at, Time.unscaledTime);
            if (!net.Connected) return;
            var w = net.WorldWriter(); w.Put(SPing); w.Put(at.x); w.Put(at.y); w.Put(at.z);
            net.SendWorld(w, DeliveryMethod.ReliableUnordered);
        }
        void ReceiveSocial(int from, NetPacketReader r)
        {
            byte type = r.GetByte();
            switch (type)
            {
                case SEmote:
                    int i = r.GetByte();
                    string caption = r.AvailableBytes > 0 ? r.GetString(32) : null;
                    if (i < EmoteIcons.Length && i != PingIndex) bubbles[from] = (i, Time.unscaledTime, caption);
                    break;
                case SEffect:
                    string effect = r.GetString(48), viewer = r.GetString(32);
                    Toast($"{viewer} → {NickOf(from)}: {EffectName(effect)}");
                    break;
                case SPull:
                    ReceivePull(from, r);
                    break;
                case SPing:
                    pings[from] = (new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Time.unscaledTime);
                    Toast(T($"{NickOf(from)} поставил метку", $"{NickOf(from)} pinged"));
                    break;
                case SStats:
                {
                    string nick = r.GetString(24);
                    var s = new Shame { Cracks = r.GetInt(), Falls = r.GetInt(), MaxFall = r.GetFloat(), Lost = r.GetFloat() };
                    if (nick == cfg.Nick.Value || s.Same(shame.TryGetValue(nick, out var old) ? old : null)) break;   // a namesake must not overwrite us
                    shame[nick] = s;
                    SaveShame();
                    break;
                }
                case SFall:
                    Toast(T($"{NickOf(from)} пролетел вниз {r.GetFloat():0} м", $"{NickOf(from)} fell {r.GetFloat():0} m"));
                    break;
                case SReset:
                    if (from != 0) break;   // the host's call
                    shame.Clear(); SaveShame();
                    Toast(T("Хост обнулил доску позора", "The host reset the shame board"));
                    break;
            }
        }

        void ResetShame()
        {
            shame.Clear(); SaveShame();
            if (net.Connected && net.IsHost) { var w = net.WorldWriter(); w.Put(SReset); net.SendWorld(w, DeliveryMethod.ReliableOrdered); }
        }

        // ---------------------------------------------------------------- GUI

        void InitSocialStyles(Font font)
        {
            emoteTex = EmoteIcons.Select(c => c != null ? LoadIcon(c) : null).ToArray();
            wheelDisc = Ring(512, 0.34f, 181f, new Color(0f, 0f, 0f, 0.78f), new Color(1f, 1f, 1f, 0.14f));
            wheelSlice = Ring(512, 0.34f, 180f / EmoteIcons.Length, new Color(1f, 0.655f, 0.153f, 0.32f), new Color(1f, 0.655f, 0.153f, 0.8f));
            wheelCenter = Ring(128, 0f, 181f, new Color(1f, 1f, 1f, 0.08f), new Color(1f, 1f, 1f, 0.2f));
            wheelCenterHot = Ring(128, 0f, 181f, new Color(1f, 0.655f, 0.153f, 0.3f), new Color(1f, 0.655f, 0.153f, 0.8f));
            triTex = Triangle(96);
            sCap = new GUIStyle(GUI.skin.label) { font = font, fontSize = 20, alignment = TextAnchor.MiddleCenter };
            sCap.normal.textColor = Orange;
            sCloseX = new GUIStyle(sCap) { fontSize = 34 };
            sCloseX.normal.textColor = Color.white;
            sHint = new GUIStyle(sDim) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            Log.LogInfo($"Emotes: {emoteTex.Count(t => t != null)} of {EmoteIcons.Length - 1} icons");
        }

        static Texture2D LoadIcon(string codepoint)
        {
            using (var s = typeof(Plugin).Assembly.GetManifestResourceStream("emote." + codepoint))
            {
                if (s == null) return null;
                var b = new byte[s.Length];
                s.Read(b, 0, b.Length);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                return t.LoadImage(b) ? t : null;
            }
        }

        // Anti-aliased ring slice pointing up: radius from inner (fraction of the outer one) to the rim, halfAngle
        // degrees either side of straight up (181 = whole ring). 1 px outline on the rim.
        static Texture2D Ring(int n, float inner, float halfAngle, Color fill, Color outline)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float c = n / 2f, R = c - 1f, Ri = R * inner;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Abs(Mathf.Atan2(dx, dy)) * Mathf.Rad2Deg;   // 0 = up (texture rows grow upward)
                    float cover = Mathf.Clamp01(R - r + 0.5f) * (Ri > 0 ? Mathf.Clamp01(r - Ri + 0.5f) : 1f) * Mathf.Clamp01((halfAngle - a) * Mathf.Deg2Rad * r + 0.5f);
                    var col = Color.Lerp(fill, outline, Mathf.Clamp01(1f - Mathf.Abs(R - r - 0.5f)));
                    col.a *= cover;
                    px[y * n + x] = col;
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        void DrawWheel()
        {
            var e = Event.current;
            var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float R = Mathf.Min(Screen.height * 0.3f, 230f), inner = R * 0.34f;
            wheelHover = labHover >= 0 ? labHover : SocialMath.Sector(e.mousePosition.x - c.x, e.mousePosition.y - c.y, inner, EmoteIcons.Length);
            var full = new Rect(c.x - R, c.y - R, 2 * R, 2 * R);
            GUI.DrawTexture(full, wheelDisc);
            if (wheelHover >= 0)
            {
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(wheelHover * 360f / EmoteIcons.Length, c);
                GUI.DrawTexture(full, wheelSlice);
                GUI.matrix = m;
            }
            float cr = inner * 0.86f;
            var centre = new Rect(c.x - cr, c.y - cr, 2 * cr, 2 * cr);
            GUI.DrawTexture(centre, wheelHover < 0 ? wheelCenterHot : wheelCenter);
            GUI.Label(centre, "×", sCloseX);
            for (int i = 0; i < EmoteIcons.Length; i++)
            {
                float a = i * Mathf.PI * 2f / EmoteIcons.Length, rr = (R + inner) / 2f;
                float s = Mathf.Min(R * 0.27f, 60f) * (i == wheelHover ? 1.18f : 1f);
                var p = new Vector2(c.x + Mathf.Sin(a) * rr, c.y - Mathf.Cos(a) * rr);
                if (i == PingIndex) { GUI.color = Color.Lerp(Color.white, PlayerColor(-1), 0.75f); GUI.DrawTexture(new Rect(p.x - s * 0.4f, p.y - s * 0.4f, s * 0.8f, s * 0.8f), triTex); GUI.color = Color.white; }
                else if (emoteTex[i] != null) GUI.DrawTexture(new Rect(p.x - s / 2, p.y - s / 2, s, s), emoteTex[i], ScaleMode.ScaleToFit);
            }
            GUI.Label(new Rect(c.x - 250, c.y + R + 6, 500, 30), wheelHover >= 0 ? EmoteNames[wheelHover] : T("Закрыть", "Close"), sCap);
            GUI.Label(new Rect(c.x - 250, c.y + R + 36, 500, 22), T($"наведите и отпустите {cfg.EmoteKey.Value} · × или Esc — закрыть", $"hover and release {cfg.EmoteKey.Value} · × or Esc to close"), sHint);
            if (e.type == EventType.MouseDown && e.button == 0) { if (wheelHover >= 0) FireEmote(wheelHover); else CloseWheel(); e.Use(); }
        }

        // Each player's colour (by player id, the same on every screen): their ping, its label and their name tag.
        static readonly Color[] Palette = { new Color(1f, 0.655f, 0.153f), new Color(0.35f, 0.8f, 1f), new Color(0.55f, 0.9f, 0.35f), new Color(1f, 0.45f, 0.7f) };
        Color PlayerColor(int id) => Palette[((id < 0 ? net.MyId : id) % Palette.Length + Palette.Length) % Palette.Length];

        // White down-pointing triangle with a soft dark rim: the ping marker (tinted per player), also the bubble's tail.
        static Texture2D Triangle(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            // Texture rows grow upward: the tip is at the bottom (low y).
            var v = new[] { new Vector2(0.08f, 0.86f), new Vector2(0.92f, 0.86f), new Vector2(0.5f, 0.1f) };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var p = new Vector2((x + 0.5f) / n, (y + 0.5f) / n);
                    float d = float.MinValue;   // signed distance in pixels, > 0 outside
                    for (int i = 0; i < 3; i++)
                    {
                        Vector2 a = v[i], b = v[(i + 1) % 3], e = (b - a).normalized, nrm = new Vector2(-e.y, e.x);   // outward: the corners go clockwise
                        d = Mathf.Max(d, Vector2.Dot(p - a, nrm) * n);
                    }
                    float rim = n * 0.06f;
                    var col = Color.Lerp(Color.white, new Color(0.1f, 0.07f, 0.05f), Mathf.Clamp01((d + rim) / 1.5f));
                    col.a = Mathf.Clamp01(0.5f - d);
                    px[y * n + x] = col;
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        void DrawSocial()
        {
            var cam = Camera.main;
            if (cam == null || !InGame() || emoteTex == null) return;
            float now = Time.unscaledTime;
            // Where our egg is drawn: GetPosition() is the physics pose, which moves in 50 Hz steps, so anything pinned
            // to it shivers against the smoothly interpolated egg on screen.
            var core = World.instance.GetEggCore();
            Vector3 me = core.yolkState ? World.instance.GetEgg().GetRawTransform().position : core.eggRigidbody.transform.position;
            foreach (var kv in bubbles.ToList())
            {
                float age = now - kv.Value.t0;
                if (age > EmoteTime) { bubbles.Remove(kv.Key); continue; }
                Vector3 at;
                if (kv.Key < 0 && (wheelOpen || labHover >= 0)) continue;   // ours would sit right on the wheel's close button
                if (kv.Key < 0) at = me;
                else if (ghosts.TryGetValue(kv.Key, out var g)) at = g.Position;
                else continue;
                // Anchored just above the egg (a friend's: above the name tag), in pixels, and always fully on screen:
                // our own egg sits low and close to the camera, a world-space offset used to throw it off the top.
                var sp = cam.WorldToScreenPoint(at + Vector3.up * (kv.Key < 0 ? 0.55f : 1.2f));
                if (sp.z <= 0 || emoteTex[kv.Value.emote] == null) continue;
                // Just the emote, no frame. A fixed size and whole-pixel position: scaling it with the camera distance and
                // drawing at fractional pixels made it shimmer every frame.
                float t = Mathf.Clamp01(age / 0.28f) - 1f, pop = 1f + 2.7f * t * t * t + 1.7f * t * t;   // ease-out-back: grows with a small overshoot
                float size = Mathf.Round(EmoteSize * Mathf.Max(0f, pop));
                float x = Mathf.Round(Mathf.Clamp(sp.x, size / 2 + 8, Screen.width - size / 2 - 8));
                float y = Mathf.Round(Mathf.Clamp(Screen.height - sp.y - size - (kv.Key < 0 ? 10f : 26f), 8f, Screen.height - size - 8f));
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01((EmoteTime - age) * 2.5f));
                GUI.DrawTexture(new Rect(x - Mathf.Round(size / 2), y, size, size), emoteTex[kv.Value.emote]);
                if (!string.IsNullOrEmpty(kv.Value.caption))
                {
                    var cr = new Rect(x - 120, y + size, 240, 20);
                    var a = GUI.color.a;
                    GUI.color = new Color(0, 0, 0, a * 0.8f); GUI.Label(new Rect(cr.x + 1, cr.y + 1, cr.width, cr.height), kv.Value.caption, tag);
                    GUI.color = new Color(1f, 0.85f, 0.6f, a); GUI.Label(cr, kv.Value.caption, tag);
                }
            }            foreach (var kv in pings.ToList())
            {
                float age = now - kv.Value.t0;
                if (age > PingTime) { pings.Remove(kv.Key); continue; }
                DrawPing(cam, kv.Key, kv.Value.at, age, me);
            }
            GUI.color = Color.white;
            if (aiming)
            {
                string hint = T("<color=#FFA727>МЕТКА</color>   кликните, куда её поставить · Esc или правая кнопка — отмена", "<color=#FFA727>PING</color>   click where to put it · Esc or right button to cancel");
                var size = sPanel.CalcSize(new GUIContent(hint));
                GUI.Box(new Rect((Screen.width - size.x) / 2, Screen.height * 0.8f, size.x, size.y), hint, sPanel);
            }
        }

        // A triangle pointing down at the spot, bobbing, with "nick · 12 м" above. Off screen (or behind us) it sits on
        // the screen edge and turns into an arrow pointing toward the spot.
        void DrawPing(Camera cam, int who, Vector3 at, float age, Vector3 me)
        {
            var sp = cam.WorldToScreenPoint(at);
            float x = sp.x, y = Screen.height - sp.y;
            const float margin = 34f;
            bool behind = sp.z < 0;
            if (behind) { x = Screen.width - x; y = Screen.height - y; }   // mirrored through the camera: flip back to its side
            var c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var dir = new Vector2(x, y) - c;
            bool off = behind || x < margin || x > Screen.width - margin || y < margin + 30 || y > Screen.height - margin;
            if (off)
            {
                if (dir.sqrMagnitude < 1f) dir = Vector2.down;
                // Slide out along the direction until the point touches the margin box.
                float sx = (Screen.width / 2f - margin) / Mathf.Max(Mathf.Abs(dir.x), 0.001f), sy = (Screen.height / 2f - margin - 15) / Mathf.Max(Mathf.Abs(dir.y), 0.001f);
                var p = c + dir * Mathf.Min(sx, sy);
                x = p.x; y = p.y;
            }
            var col = Color.Lerp(Color.white, PlayerColor(who), 0.75f);
            float alpha = Mathf.Clamp01(PingTime - age) * Mathf.Clamp01(age * 8f);
            float w = 30f * (1f + 0.35f * Mathf.Max(0f, 1f - age * 4f));   // a little pop when placed
            var m = GUI.matrix;
            if (off) GUIUtility.RotateAroundPivot(Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f, new Vector2(x, y));   // the tip points at the spot
            float bob = off ? 0f : 5f * Mathf.Abs(Mathf.Sin(age * 3.2f));
            var tri = new Rect(Mathf.Round(x - w / 2), Mathf.Round(y - w * 0.95f - bob), Mathf.Round(w), Mathf.Round(w));   // tip (bottom of the texture) on the spot, whole pixels
            GUI.color = new Color(0, 0, 0, alpha * 0.45f); GUI.DrawTexture(new Rect(tri.x + 2, tri.y + 3, w, w), triTex);
            GUI.color = new Color(col.r, col.g, col.b, alpha); GUI.DrawTexture(tri, triTex);
            GUI.matrix = m;
            string label = T($"{(who < 0 ? cfg.Nick.Value : NickOf(who))} · {Vector3.Distance(me, at):0} м", $"{(who < 0 ? cfg.Nick.Value : NickOf(who))} · {Vector3.Distance(me, at):0} m");
            var r = off ? new Rect(Mathf.Clamp(x - 120, 4, Screen.width - 244), Mathf.Clamp(y + 18, 4, Screen.height - 26), 240, 22)
                        : new Rect(x - 120, y - w - bob - 24, 240, 22);
            GUI.color = new Color(0, 0, 0, alpha * 0.8f); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, tag);
            GUI.color = new Color(col.r, col.g, col.b, alpha); GUI.Label(r, label, tag);
            GUI.color = Color.white;
        }
        // "Доска позора", worst first: most height lost.
        List<string> ShameLines()
        {
            var lines = new List<string>();
            int rank = 0;
            foreach (var kv in shame.OrderByDescending(kv => kv.Value.Lost).ThenByDescending(kv => kv.Value.Cracks))
            {
                var s = kv.Value;
                string who = kv.Key == cfg.Nick.Value ? T($"{kv.Key} (вы)", $"{kv.Key} (you)") : kv.Key;
                lines.Add($"{(++rank == 1 ? "<color=#FFA727>" : "")}{rank}. {who}{(rank == 1 ? "</color>" : "")}   <size=13><color=#BFBFBF>" + T($"потерял {s.Lost:0} м · падений {s.Falls} · худшее {s.MaxFall:0} м · разбился {s.Cracks}", $"lost {s.Lost:0} m · falls {s.Falls} · worst {s.MaxFall:0} m · cracks {s.Cracks}") + "</color></size>");
            }
            return lines;
        }

        void DrawShameBoard()
        {
            GUILayout.Label(T("ДОСКА ПОЗОРА", "SHAME BOARD"), sHead);
            var lines = ShameLines();
            if (lines.Count == 0) GUILayout.Label(T("Пока никто не падал. Пока.", "Nobody has fallen yet. Yet."), sDim);
            foreach (var l in lines) GUILayout.Label(l, sText);
            GUILayout.Label(T($"Падение — когда яйцо остановилось на {FallTracker.MinDrop:0}+ м ниже, чем было. От {BigFall:0} м об этом узнают все.", $"A fall is when the egg comes to rest {FallTracker.MinDrop:0}+ m lower than it was. From {BigFall:0} m everyone hears about it."), sDim);
            if ((!net.Active || net.IsHost) && lines.Count > 0 && GUILayout.Button(net.Active ? T("Обнулить доску позора у всех", "Reset the shame board for everyone") : T("Обнулить доску позора", "Reset the shame board"), sBtn)) ResetShame();
        }

        void UpdateCursor()
        {
            bool free = menuOpen || wheelOpen || aiming;
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
            // The menu takes the keyboard (typing a nick must not roll the egg); every overlay takes the mouse (no camera turn).
            try { Rewired.ReInput.controllers.Keyboard.enabled = !menuOpen; Rewired.ReInput.controllers.Mouse.enabled = !free; } catch { }
        }

        int labHover = -1;   // lab screenshots: draw the wheel with this slice hovered
    }
}
