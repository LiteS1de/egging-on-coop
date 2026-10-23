using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using UnityEngine;

namespace EggCoop
{
    // Emotes over the eggs, ping markers and the shame board. Hold the emote key (G): a wheel opens, hover a slice
    // and let go to send it; the centre "×" (or Esc) closes it. "Метка" then asks for a click on the spot to mark.
    public partial class Plugin
    {
        const byte SEmote = CoopNet.SocialBase, SStats = SEmote + 1, SPing = SEmote + 2, SFall = SEmote + 3, SReset = SEmote + 4;
        const float EmoteTime = 3.5f, PingTime = 12f, BigFall = 15f;
        // Twemoji 15.1 (CC-BY 4.0), embedded as emote.<codepoint>.
        static readonly string[] EmoteIcons = { "1f44b", "1f44d", "1f602", "1f631", "1f621", "1f64f", "1f480", "1f4cd" };
        static readonly string[] EmoteNames = { "Привет", "Класс!", "Ха-ха", "Ааа!", "Злюсь", "Помогите!", "RIP", "Метка" };
        const int PingIndex = 7;

        Texture2D[] emoteTex;
        Texture2D wheelDisc, wheelSlice, wheelCenter, wheelCenterHot;
        GUIStyle sBubble, sCap, sCloseX, sHint;
        bool wheelOpen, aiming;
        int wheelHover = -1;
        float lastEmoteAt = -10f, nextStats;
        readonly Dictionary<int, (int emote, float t0)> bubbles = new Dictionary<int, (int, float)>();   // player id, -1 = us
        readonly Dictionary<int, (Vector3 at, float t0)> pings = new Dictionary<int, (Vector3, float)>();
        Dictionary<string, Shame> shame = new Dictionary<string, Shame>();
        static readonly FallTracker fall = new FallTracker();
        int lastCracks = -1;

        Shame MyShame => shame.TryGetValue(cfg.Nick.Value, out var s) ? s : shame[cfg.Nick.Value] = new Shame();
        void SaveShame() => cfg.ShameStats.Value = Shame.Format(shame);

        public static string NickOf(int id) => instance != null && instance.ghosts.TryGetValue(id, out var g) ? g.Nick : id == 0 ? "Хост" : "Друг";

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
            Toast($"Вы пролетели вниз {drop:0} м");
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
            return false;
        }

        void CloseWheel() { wheelOpen = false; wheelHover = -1; UpdateCursor(); }

        void FireEmote(int i)
        {
            CloseWheel();
            if (i == PingIndex) { aiming = true; UpdateCursor(); return; }
            if (Time.unscaledTime - lastEmoteAt < 0.7f) return;   // no spamming
            lastEmoteAt = Time.unscaledTime;
            bubbles[-1] = (i, Time.unscaledTime);
            if (!net.Connected) return;
            var w = net.WorldWriter(); w.Put(SEmote); w.Put((byte)i);
            net.SendWorld(w, DeliveryMethod.ReliableUnordered);
        }

        void PlacePing(Vector2 screen)
        {
            aiming = false; UpdateCursor();
            var cam = Camera.main;
            if (cam == null || !InGame()) return;
            // First solid thing under the cursor that is not an egg (ours or a friend's).
            foreach (var h in Physics.RaycastAll(cam.ScreenPointToRay(screen), 500f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (h.collider.GetComponentInParent<EggCoreData>() != null || h.collider.GetComponentInParent<RawEggController>() != null
                    || h.collider.transform.root.name.StartsWith("EggCoop_Ghost")) continue;
                pings[-1] = (h.point, Time.unscaledTime);
                if (net.Connected)
                {
                    var w = net.WorldWriter(); w.Put(SPing); w.Put(h.point.x); w.Put(h.point.y); w.Put(h.point.z);
                    net.SendWorld(w, DeliveryMethod.ReliableUnordered);
                }
                return;
            }
            Toast("Туда метку не поставить — кликните по земле или предмету");
        }

        void ReceiveSocial(int from, NetPacketReader r)
        {
            byte type = r.GetByte();
            switch (type)
            {
                case SEmote:
                    int i = r.GetByte();
                    if (i < EmoteIcons.Length && i != PingIndex) bubbles[from] = (i, Time.unscaledTime);
                    break;
                case SPing:
                    pings[from] = (new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Time.unscaledTime);
                    Toast($"{NickOf(from)} поставил метку");
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
                    Toast($"{NickOf(from)} пролетел вниз {r.GetFloat():0} м");
                    break;
                case SReset:
                    if (from != 0) break;   // the host's call
                    shame.Clear(); SaveShame();
                    Toast("Хост обнулил доску позора");
                    break;
            }
        }

        void ResetShame()
        {
            shame.Clear(); SaveShame();
            if (net.Connected && net.IsHost) { var w = net.WorldWriter(); w.Put(SReset); net.SendWorld(w, DeliveryMethod.ReliableOrdered); }
        }

        // ---------------------------------------------------------------- GUI

        void InitSocialStyles(Font font, Texture2D panel, RectOffset panelBorder)
        {
            emoteTex = EmoteIcons.Select(LoadIcon).ToArray();
            wheelDisc = Ring(512, 0.34f, 181f, new Color(0f, 0f, 0f, 0.78f), new Color(1f, 1f, 1f, 0.14f));
            wheelSlice = Ring(512, 0.34f, 180f / EmoteIcons.Length, new Color(1f, 0.655f, 0.153f, 0.32f), new Color(1f, 0.655f, 0.153f, 0.8f));
            wheelCenter = Ring(128, 0f, 181f, new Color(1f, 1f, 1f, 0.08f), new Color(1f, 1f, 1f, 0.2f));
            wheelCenterHot = Ring(128, 0f, 181f, new Color(1f, 0.655f, 0.153f, 0.3f), new Color(1f, 0.655f, 0.153f, 0.8f));
            sBubble = new GUIStyle { border = panelBorder };
            sBubble.normal.background = panel;
            sCap = new GUIStyle(GUI.skin.label) { font = font, fontSize = 20, alignment = TextAnchor.MiddleCenter };
            sCap.normal.textColor = Orange;
            sCloseX = new GUIStyle(sCap) { fontSize = 34 };
            sCloseX.normal.textColor = Color.white;
            sHint = new GUIStyle(sDim) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            Log.LogInfo($"Emotes: {emoteTex.Count(t => t != null)} of {EmoteIcons.Length} icons");
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
                if (emoteTex[i] != null) GUI.DrawTexture(new Rect(p.x - s / 2, p.y - s / 2, s, s), emoteTex[i], ScaleMode.ScaleToFit);
            }
            GUI.Label(new Rect(c.x - 250, c.y + R + 6, 500, 30), wheelHover >= 0 ? EmoteNames[wheelHover] : "Закрыть", sCap);
            GUI.Label(new Rect(c.x - 250, c.y + R + 36, 500, 22), $"наведите и отпустите {cfg.EmoteKey.Value} · × или Esc — закрыть", sHint);
            if (e.type == EventType.MouseDown && e.button == 0) { if (wheelHover >= 0) FireEmote(wheelHover); else CloseWheel(); e.Use(); }
        }

        void DrawSocial()
        {
            var cam = Camera.main;
            if (cam == null || !InGame() || emoteTex == null) return;
            float now = Time.unscaledTime;
            Vector3 me = World.instance.GetEgg().GetPosition();
            foreach (var kv in bubbles.ToList())
            {
                float age = now - kv.Value.t0;
                if (age > EmoteTime) { bubbles.Remove(kv.Key); continue; }
                Vector3 at;
                if (kv.Key < 0) at = me;
                else if (ghosts.TryGetValue(kv.Key, out var g)) at = g.Position;
                else continue;
                var sp = cam.WorldToScreenPoint(at + Vector3.up * 1.9f);
                if (sp.z <= 0 || emoteTex[kv.Value.emote] == null) continue;
                // Pops in with a small overshoot, fades out over the last half second.
                float pop = age < 0.18f ? Mathf.Sin(age / 0.18f * Mathf.PI * 0.75f) / 0.707f : 1f;
                float size = Mathf.Clamp(900f / sp.z, 44f, 76f) * Mathf.Max(0.3f, pop);
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01((EmoteTime - age) * 2f));
                var box = new Rect(sp.x - size / 2, Screen.height - sp.y - size, size, size);
                GUI.Box(box, GUIContent.none, sBubble);
                GUI.DrawTexture(new Rect(box.x + size * 0.14f, box.y + size * 0.14f, size * 0.72f, size * 0.72f), emoteTex[kv.Value.emote], ScaleMode.ScaleToFit);
            }
            foreach (var kv in pings.ToList())
            {
                float age = now - kv.Value.t0;
                if (age > PingTime) { pings.Remove(kv.Key); continue; }
                var sp = cam.WorldToScreenPoint(kv.Value.at);
                // Behind the camera or off screen: pinned to the nearest edge so it can still be found.
                float x = sp.x, y = Screen.height - sp.y;
                if (sp.z < 0)
                {
                    // Projected through the camera it lands mirrored and anywhere on screen: push it out to the edge on its side.
                    var d = new Vector2(Screen.width / 2f - x, Screen.height / 2f - y);
                    if (d.sqrMagnitude < 1f) d = Vector2.up;
                    d = d.normalized * (Screen.width + Screen.height);
                    x = Screen.width / 2f + d.x; y = Screen.height / 2f + d.y;
                }
                bool edge = sp.z < 0 || x < 30 || x > Screen.width - 30 || y < 60 || y > Screen.height - 30;
                x = Mathf.Clamp(x, 30, Screen.width - 30); y = Mathf.Clamp(y, 60, Screen.height - 30);
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(PingTime - age) * (edge ? 0.75f : 1f));
                float s = 40f + 8f * Mathf.Max(0f, 1f - age * 3f);   // a short bounce when placed
                if (emoteTex[PingIndex] != null) GUI.DrawTexture(new Rect(x - s / 2, y - s, s, s), emoteTex[PingIndex], ScaleMode.ScaleToFit);
                string label = $"{(kv.Key < 0 ? cfg.Nick.Value : NickOf(kv.Key))} · {Vector3.Distance(me, kv.Value.at):0} м";
                var r = new Rect(x - 120, y + 2, 240, 22);
                var col = GUI.color;
                GUI.color = new Color(0, 0, 0, col.a); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, tag);
                GUI.color = col; GUI.Label(r, label, tag);
            }
            GUI.color = Color.white;
            if (aiming)
            {
                const string hint = "<color=#FFA727>МЕТКА</color>   кликните, куда её поставить · Esc или правая кнопка — отмена";
                var size = sPanel.CalcSize(new GUIContent(hint));
                GUI.Box(new Rect((Screen.width - size.x) / 2, Screen.height * 0.8f, size.x, size.y), hint, sPanel);
            }
        }

        // "Доска позора", worst first: most height lost.
        List<string> ShameLines()
        {
            var lines = new List<string>();
            int rank = 0;
            foreach (var kv in shame.OrderByDescending(kv => kv.Value.Lost).ThenByDescending(kv => kv.Value.Cracks))
            {
                var s = kv.Value;
                string who = kv.Key == cfg.Nick.Value ? $"{kv.Key} (вы)" : kv.Key;
                lines.Add($"{(++rank == 1 ? "<color=#FFA727>" : "")}{rank}. {who}{(rank == 1 ? "</color>" : "")}   <size=13><color=#BFBFBF>потерял {s.Lost:0} м · падений {s.Falls} · худшее {s.MaxFall:0} м · разбился {s.Cracks}</color></size>");
            }
            return lines;
        }

        void DrawShameBoard()
        {
            GUILayout.Label("ДОСКА ПОЗОРА", sHead);
            var lines = ShameLines();
            if (lines.Count == 0) GUILayout.Label("Пока никто не падал. Пока.", sDim);
            foreach (var l in lines) GUILayout.Label(l, sText);
            GUILayout.Label($"Падение — когда яйцо остановилось на {FallTracker.MinDrop:0}+ м ниже, чем было. От {BigFall:0} м об этом узнают все.", sDim);
            if ((!net.Active || net.IsHost) && lines.Count > 0 && GUILayout.Button(net.Active ? "Обнулить доску позора у всех" : "Обнулить доску позора", sBtn)) ResetShame();
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
