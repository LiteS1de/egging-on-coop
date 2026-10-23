using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Rendering;

using static EggCoop.Lang;

namespace EggCoop
{
    // Viewer effects for streamers. Viewers pay (Twitch channel points / bits, see Twitch.cs) and the effect plays on
    // the streamer's egg. Every effect can be switched off and priced by the streamer in F9; one effect per gap, each
    // with its own cooldown; nothing runs while cracked, frozen or paused (it waits, then is refunded). Effects never
    // crack the egg by themselves. Friends see a toast about every effect, and "Друга — к стримеру" asks before moving
    // anyone: each player allows it in their own F9.
    public partial class Plugin
    {
        const byte SEffect = SEmote + 5, SPull = SEmote + 6;

        class EffectDef
        {
            public string Id, Ru, En, RuGroup, EnGroup, RuDesc, EnDesc;
            public string Name => T(Ru, En);
            public string Group => T(RuGroup, EnGroup);
            public string Desc => T(RuDesc, EnDesc);   // the tooltip in F9
            public float Duration;          // 0 = instant
            public int Points, Bits, Cooldown;
        }

        // Default prices: channel points come at ~300-500 an hour per viewer, 1 bit is about 1 cent.
        static readonly EffectDef[] Effects =
        {
            new EffectDef { Id = "emote", Ru = "Эмоция от зрителя", En = "Viewer emote", RuDesc = "Над вашим яйцом на 3,5 с всплывает случайная эмоция с ником зрителя. Её видят и друзья. На игру не влияет.", EnDesc = "A random emote with the viewer's nick pops up over your egg for 3.5 s. Friends see it too. No effect on the game.", RuGroup = "Безобидные", EnGroup = "Harmless", Points = 100, Bits = 10, Cooldown = 10 },
            new EffectDef { Id = "confetti", Ru = "Конфетти", En = "Confetti", RuDesc = "Около трёх секунд на экран сыплется конфетти. На игру не влияет.", EnDesc = "Confetti rains over the screen for about three seconds. No effect on the game.", RuGroup = "Безобидные", EnGroup = "Harmless", Points = 200, Bits = 20, Cooldown = 20 },
            new EffectDef { Id = "wind", Ru = "Порыв ветра", En = "Gust of wind", RuDesc = "Толкает яйцо в случайную сторону (около 3,5 м/с вбок и немного вверх). Может сдвинуть с края.", EnDesc = "Pushes the egg in a random direction (about 3.5 m/s sideways and a bit up). Can knock it off an edge.", RuGroup = "Толчки", EnGroup = "Pushes", Points = 500, Bits = 50, Cooldown = 30 },
            new EffectDef { Id = "hop", Ru = "Подпрыгнуть", En = "Hop", RuDesc = "Подбрасывает яйцо прямо вверх примерно на полметра.", EnDesc = "Tosses the egg straight up about half a metre.", RuGroup = "Толчки", EnGroup = "Pushes", Points = 400, Bits = 40, Cooldown = 30 },
            new EffectDef { Id = "moon", Ru = "Лунная гравитация", En = "Moon gravity", RuDesc = "На 15 с гравитация втрое слабее: прыжки выше, падения медленнее. Действует на всю физику у вас в игре.", EnDesc = "Gravity is three times weaker for 15 s: higher jumps, slower falls. Affects all physics in your game.", RuGroup = "Хаос", EnGroup = "Chaos", Duration = 15, Points = 1500, Bits = 150, Cooldown = 90 },
            new EffectDef { Id = "slowmo", Ru = "Замедление", En = "Slow motion", RuDesc = "На 10 с вся игра у вас идёт на 60 % скорости. У друзей время не меняется.", EnDesc = "Your whole game runs at 60% speed for 10 s. Friends' time is unchanged.", RuGroup = "Хаос", EnGroup = "Chaos", Duration = 10, Points = 1500, Bits = 150, Cooldown = 90 },
            new EffectDef { Id = "ice", Ru = "Скользкое яйцо", En = "Slippery egg", RuDesc = "На 15 с у яйца пропадает трение: оно скользит, как по льду, и хуже держится на склонах.", EnDesc = "The egg loses its grip for 15 s: it slides as if on ice and holds worse on slopes.", RuGroup = "Хаос", EnGroup = "Chaos", Duration = 15, Points = 1500, Bits = 150, Cooldown = 90 },
            new EffectDef { Id = "flip", Ru = "Камера вверх ногами", En = "Upside-down camera", RuDesc = "На 10 с картинка переворачивается вверх ногами. Управление остаётся прежним.", EnDesc = "The picture turns upside down for 10 s. Controls stay the same.", RuGroup = "Хаос", EnGroup = "Chaos", Duration = 10, Points = 2000, Bits = 200, Cooldown = 120 },
            new EffectDef { Id = "tofriend", Ru = "Телепорт к другу", En = "Teleport to a friend", RuDesc = "Переносит вас к случайному другу, как телепорт на T. Если друзей нет в сети, эффект ждёт минуту, потом баллы возвращаются.", EnDesc = "Moves you to a random friend, like the T teleport. If no friends are online, the effect waits a minute, then the points are refunded.", RuGroup = "Кооператив", EnGroup = "Co-op", Points = 2500, Bits = 250, Cooldown = 180 },
            new EffectDef { Id = "pullfriend", Ru = "Друга — к стримеру", En = "Friend to the streamer", RuDesc = "Переносит случайного друга к вам. Только для друзей, которые разрешили это у себя в F9 → «Стрим».", EnDesc = "Moves a random friend to you. Only friends who allowed it in their F9, Stream tab.", RuGroup = "Кооператив", EnGroup = "Co-op", Points = 2500, Bits = 250, Cooldown = 180 },
        };

        class EffectCfg { public ConfigEntry<bool> On; public ConfigEntry<int> Points, Bits, Cooldown; }
        readonly Dictionary<string, EffectCfg> effCfg = new Dictionary<string, EffectCfg>();
        ConfigEntry<bool> streamEffects, allowPull;
        ConfigEntry<int> streamGap;
        ConfigEntry<KeyCode> streamPauseKey;
        bool streamPaused;
        readonly StreamQueue streamQ = new StreamQueue();
        readonly Dictionary<string, float> activeUntil = new Dictionary<string, float>();
        Vector3 savedGravity;
        PhysicMaterial savedEggMat, iceMat;
        bool flipped;
        readonly List<(Vector2 p, Vector2 v, Color c, float rot, float spin)> confetti = new List<(Vector2, Vector2, Color, float, float)>();
        public static System.Action<StreamQueue.Req, bool> OnEffectDone;   // (request, ran) -> Twitch fulfills or refunds

        void InitStream()
        {
            streamEffects = Config.Bind("Stream", "Effects", false, T("Эффекты от зрителей (Twitch: баллы канала и bits)", "Viewer effects (Twitch: channel points and bits)"));
            allowPull = Config.Bind("Stream", "AllowFriendStreamerPull", true, T("Разрешить друзьям-стримерам переносить моё яйцо к себе (эффект зрителей)", "Let streamer friends pull my egg to them (a viewer effect)"));
            streamGap = Config.Bind("Stream", "GapSeconds", 12, T("Не чаще одного эффекта за столько секунд", "At most one effect per this many seconds"));
            streamPauseKey = Config.Bind("Stream", "PauseKey", KeyCode.F10, T("Пауза эффектов от зрителей", "Pause viewer effects"));
            foreach (var d in Effects)
                effCfg[d.Id] = new EffectCfg
                {
                    On = Config.Bind("Stream." + d.Id, "Enabled", true, d.En),
                    Points = Config.Bind("Stream." + d.Id, "ChannelPoints", d.Points, T("Цена в баллах канала (0 — не продавать за баллы)", "Price in channel points (0: not sold for points)")),
                    Bits = Config.Bind("Stream." + d.Id, "Bits", d.Bits, T("Цена в bits (0 — не продавать за bits)", "Price in bits (0: not sold for bits)")),
                    Cooldown = Config.Bind("Stream." + d.Id, "CooldownSeconds", d.Cooldown, T("Откат эффекта, секунд", "Effect cooldown, seconds")),
                };
            streamQ.Cooldown = id => effCfg.TryGetValue(id, out var c) ? c.Cooldown.Value : 0;
            // The flip turns the picture, not the camera rig: roll just before this camera renders, undo right after.
            RenderPipelineManager.beginCameraRendering += (_, cam) => FlipCamera(cam, true);
            RenderPipelineManager.endCameraRendering += (_, cam) => FlipCamera(cam, false);
            Camera.onPreCull += cam => FlipCamera(cam, true);
            Camera.onPostRender += cam => FlipCamera(cam, false);
        }

        static EffectDef Def(string id) => Effects.FirstOrDefault(d => d.Id == id);
        // A friend's toast carries the effect name in their language.
        static string EffectName(string name) => Effects.FirstOrDefault(d => d.Ru == name || d.En == name)?.Name ?? name;
        bool Active(string id) => activeUntil.TryGetValue(id, out var t) && Time.unscaledTime < t;

        // Called by the Twitch side (or a test button). false = not taken (unknown, switched off): refund it.
        public bool RequestEffect(string id, string viewer, string source, object tag = null)
        {
            var d = Def(id);
            if (d == null || !streamEffects.Value || !effCfg[id].On.Value) return false;
            streamQ.Add(new StreamQueue.Req { Effect = id, Viewer = viewer, Source = source, Tag = tag, At = Time.unscaledTime });
            Log.LogInfo($"Stream: {viewer} ({source}) asked for {id}, {streamQ.Count} waiting");
            return true;
        }

        bool CanRunEffect(string id)
        {
            if (streamPaused || !InGame() || Time.timeScale <= 0f) return false;
            var core = World.instance.GetEggCore();
            if (core.yolkState || core.freeze) return false;
            if (Def(id).Duration > 0 && Active(id)) return false;
            if (id == "tofriend" || id == "pullfriend") return ghosts.Values.Any(g => g.Fresh && !g.Yolk);
            return true;
        }

        void StreamTick(bool inGame)
        {
            float now = Time.unscaledTime;
            streamQ.Gap = streamGap.Value;
            // Timed effects: keep them applied while active, undo when they end (or when we leave the game).
            if (Active("slowmo") && inGame) { if (Time.timeScale > 0f) Time.timeScale = 0.6f; }
            else if (activeUntil.Remove("slowmo") && Time.timeScale > 0f) Time.timeScale = 1f;
            if (!(Active("moon") && inGame) && activeUntil.Remove("moon")) Physics.gravity = savedGravity;
            if (!(Active("ice") && inGame) && activeUntil.Remove("ice") && InGame()) World.instance.GetEggCore().eggCollider.sharedMaterial = savedEggMat;
            if (!Active("flip")) activeUntil.Remove("flip");

            var expired = new List<StreamQueue.Req>();
            var next = streamQ.Next(now, CanRunEffect, expired);
            foreach (var r in expired) { Log.LogInfo($"Stream: {r.Effect} for {r.Viewer} timed out"); OnEffectDone?.Invoke(r, false); }
            if (next == null) return;
            bool ran = false;
            try { ran = RunEffect(next); } catch (System.Exception e) { Warn("stream effect", e); }
            OnEffectDone?.Invoke(next, ran);
        }

        bool RunEffect(StreamQueue.Req r)
        {
            var d = Def(r.Effect);
            var core = World.instance.GetEggCore();
            var rb = core.eggRigidbody;
            switch (r.Effect)
            {
                case "emote":
                    int i = Random.Range(0, PingIndex);
                    bubbles[-1] = (i, Time.unscaledTime, r.Viewer);
                    SendEmote(i, r.Viewer);
                    break;
                case "confetti":
                    for (int n = 0; n < 90; n++)
                        confetti.Add((new Vector2(Random.Range(0f, Screen.width), Random.Range(-Screen.height * 0.6f, -10f)), new Vector2(Random.Range(-40f, 40f), Random.Range(120f, 260f)),
                            Color.HSVToRGB(Random.value, 0.75f, 1f), Random.Range(0f, 360f), Random.Range(-360f, 360f)));
                    break;
                case "wind":
                    var dir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
                    rb.AddForce(dir * 3.5f + Vector3.up * 1.2f, ForceMode.VelocityChange);
                    break;
                case "hop":
                    rb.AddForce(Vector3.up * 5f, ForceMode.VelocityChange);
                    break;
                case "moon":
                    savedGravity = Physics.gravity;
                    Physics.gravity = savedGravity * 0.35f;
                    break;
                case "ice":
                    savedEggMat = core.eggCollider.sharedMaterial;
                    if (iceMat == null) iceMat = new PhysicMaterial("EggCoop ice") { dynamicFriction = 0f, staticFriction = 0f, frictionCombine = PhysicMaterialCombine.Minimum, bounciness = savedEggMat != null ? savedEggMat.bounciness : 0f };
                    core.eggCollider.sharedMaterial = iceMat;
                    break;
                case "tofriend":
                {
                    var friends = ghosts.Values.Where(g => g.Fresh && !g.Yolk).ToList();
                    if (friends.Count == 0) return false;
                    TryTeleport(friends[Random.Range(0, friends.Count)]);
                    break;
                }
                case "pullfriend":
                {
                    var ids = ghosts.Where(kv => kv.Value.Fresh && !kv.Value.Yolk).Select(kv => kv.Key).ToList();
                    if (ids.Count == 0 || !net.Connected) return false;
                    int target = ids[Random.Range(0, ids.Count)];
                    var at = World.instance.GetEgg().GetPosition();
                    var w = net.WorldWriter(); w.Put(SPull); w.Put((byte)target); w.Put(at.x); w.Put(at.y); w.Put(at.z); w.Put(r.Viewer ?? "", 32);
                    net.SendWorld(w, DeliveryMethod.ReliableOrdered);
                    break;
                }
            }
            if (d.Duration > 0) activeUntil[r.Effect] = Time.unscaledTime + d.Duration;
            string who = string.IsNullOrEmpty(r.Viewer) ? T("Зритель", "Viewer") : r.Viewer;
            Toast($"{who}: {d.Name}{(d.Duration > 0 ? T($" на {d.Duration:0} с", $" for {d.Duration:0} s") : "")}");
            if (net.Connected) { var w = net.WorldWriter(); w.Put(SEffect); w.Put(d.Name, 48); w.Put(who, 32); net.SendWorld(w, DeliveryMethod.ReliableUnordered); }
            Log.LogInfo($"Stream: ran {r.Effect} for {who} ({r.Source})");
            return true;
        }

        void StopAllEffects()
        {
            if (activeUntil.ContainsKey("slowmo") && Time.timeScale > 0f) Time.timeScale = 1f;
            if (activeUntil.ContainsKey("moon")) Physics.gravity = savedGravity;
            if (activeUntil.ContainsKey("ice") && InGame()) World.instance.GetEggCore().eggCollider.sharedMaterial = savedEggMat;
            activeUntil.Clear();
            confetti.Clear();
        }

        void FlipCamera(Camera cam, bool before)
        {
            if (cam == null || cam != Camera.main) return;
            if (before && !flipped && Active("flip")) { cam.transform.Rotate(0f, 0f, 180f, Space.Self); flipped = true; }
            else if (!before && flipped) { cam.transform.Rotate(0f, 0f, -180f, Space.Self); flipped = false; }
        }

        // A friend streams and their viewers bought "bring a friend": we get moved next to them, if we allow it.
        void ReceivePull(int from, LiteNetLib.NetPacketReader r)
        {
            int target = r.GetByte();
            var at = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            string viewer = r.GetString(32);
            if (target != net.MyId) return;
            if (!allowPull.Value) { Toast(T($"Зрители {NickOf(from)} хотели перенести вас к нему — выключено в F9", $"{NickOf(from)}'s viewers wanted to pull you over: turned off in F9")); return; }
            if (!InGame() || World.instance.GetEggCore().freeze) return;
            Place(at + Vector3.up * 1.6f);
            tpSeq++;   // friends snap our copy instead of sweeping it across the level
            Toast(T($"Зритель {viewer} перенёс вас к {NickOf(from)}", $"Viewer {viewer} moved you to {NickOf(from)}"));
        }

        void DrawConfetti()
        {
            if (confetti.Count == 0) return;
            float dt = Time.unscaledDeltaTime;
            var m = GUI.matrix;
            for (int i = confetti.Count - 1; i >= 0; i--)
            {
                var c = confetti[i];
                if (Event.current.type == EventType.Repaint)
                {
                    c.v.y += 90f * dt; c.v.x += Mathf.Sin(Time.unscaledTime * 3f + i) * 30f * dt;
                    c.p += c.v * dt; c.rot += c.spin * dt;
                    confetti[i] = c;
                }
                if (c.p.y > Screen.height + 20) { confetti.RemoveAt(i); continue; }
                GUIUtility.RotateAroundPivot(c.rot, c.p);
                GUI.color = c.c;
                GUI.DrawTexture(new Rect(c.p.x - 5, c.p.y - 3, 10, 6), Texture2D.whiteTexture);
                GUI.matrix = m;
            }
            GUI.color = Color.white;
        }

        // F9 → «Стрим»: master switch, pause, gap, and every effect with its prices, cooldown and a test button.
        void DrawStreamSection()
        {
            GUILayout.Label(T("ЭФФЕКТЫ ОТ ЗРИТЕЛЕЙ", "VIEWER EFFECTS"), sHead);
            streamEffects.Value = EggToggle(streamEffects.Value, T("Зрители могут влиять на игру (баллы канала и bits на Twitch)", "Viewers can affect the game (Twitch channel points and bits)"));
            DrawTwitchRow();
            if (streamEffects.Value)
            {
                bool p = EggToggle(streamPaused, T($"Пауза эффектов ({streamPauseKey.Value})", $"Pause effects ({streamPauseKey.Value})"));
                if (p != streamPaused) SetStreamPaused(p);
                GUILayout.BeginHorizontal();
                GUILayout.Label(T("Не чаще одного эффекта раз в, с", "At most one effect every, s"), sText, GUILayout.Width(250));
                streamGap.Value = IntField(streamGap.Value, 50);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("", sDim, GUILayout.Width(230));
                GUILayout.Label(new GUIContent(T("баллы", "points"), T("Цена в баллах канала. 0 — эффект не продаётся за баллы.", "Price in channel points. 0: the effect is not sold for points.")), sDim, GUILayout.Width(62));
                GUILayout.Label(new GUIContent("bits", T("Цена в bits. Зритель пишет в сообщении с bits название эффекта. 0 — не продаётся за bits.", "Price in bits. The viewer writes the effect's name in the cheer message. 0: not sold for bits.")), sDim, GUILayout.Width(52));
                GUILayout.Label(new GUIContent(T("откат", "cd"), T("Сколько секунд эффект нельзя заказать снова после срабатывания.", "How many seconds the effect can't be ordered again after it runs.")), sDim, GUILayout.Width(46));
                GUILayout.EndHorizontal();
                string group = null;
                foreach (var d in Effects)
                {
                    if (d.Group != group) { group = d.Group; GUILayout.Label(group, sDim); }
                    var c = effCfg[d.Id];
                    GUILayout.BeginHorizontal();
                    float left = streamQ.CooldownLeft(d.Id, Time.unscaledTime);
                    var was = (c.On.Value, c.Points.Value, c.Cooldown.Value);
                    c.On.Value = EggToggleW(c.On.Value, d.Name + (Active(d.Id) ? " ●" : left > 0 ? $" ({left:0})" : ""), 230, d.Desc);
                    c.Points.Value = IntField(c.Points.Value, 58);
                    c.Bits.Value = IntField(c.Bits.Value, 48);
                    c.Cooldown.Value = IntField(c.Cooldown.Value, 42);
                    if (was != (c.On.Value, c.Points.Value, c.Cooldown.Value)) MarkRewardsDirty();   // the channel point reward follows
                    if (GUILayout.Button(T("Тест", "Test"), sBtn, GUILayout.Width(64)) && !RequestEffect(d.Id, cfg.Nick.Value, "test")) Toast(T("Эффект выключен", "The effect is off"));
                    GUILayout.EndHorizontal();
                }
                if (streamQ.Count > 0) GUILayout.Label(T($"В очереди: {string.Join(", ", streamQ.Pending.Select(q => Def(q.Effect).Name))}", $"Queued: {string.Join(", ", streamQ.Pending.Select(q => Def(q.Effect).Name))}"), sDim);
                GUILayout.Label(T("Эффекты ждут, пока яйцо цело и игра не на паузе; не дождавшись за минуту, баллы возвращаются зрителю. Эффекты сами яйцо не разбивают.", "Effects wait until the egg is whole and the game is not paused; after a minute of waiting the points go back to the viewer. Effects never crack the egg themselves."), sDim);
            }
            allowPull.Value = EggToggle(allowPull.Value, T("Друзья-стримеры могут переносить меня к себе (эффект их зрителей)", "Streamer friends can pull me to them (their viewers' effect)"));
        }

        // The hovered control's tooltip as a small panel next to the mouse, kept inside the window. Call last in the window.
        void DrawTooltip(Rect window)
        {
            var m = Event.current.mousePosition;
#if LAB
            if (labTip != null) { GUI.tooltip = labTip; m = labTipAt; }
#endif
            if (string.IsNullOrEmpty(GUI.tooltip)) return;
            var content = new GUIContent(GUI.tooltip);
            float w = Mathf.Min(300f, window.width - 20f), h = sTip.CalcHeight(content, w);
            float x = Mathf.Clamp(m.x + 16f, 6f, window.width - w - 6f), y = m.y + 20f;
            if (y + h > window.height - 6f) y = m.y - h - 8f;   // no room below: show it above the cursor
            GUI.Box(new Rect(x, y, w, h), content, sTip);
        }

#if LAB
        string labTip; Vector2 labTipAt;   // lab screenshots: a tooltip as if the mouse were there
#endif

        void SetStreamPaused(bool p)
        {
            streamPaused = p;
            Toast(p ? T("Эффекты от зрителей на паузе", "Viewer effects paused") : T("Эффекты от зрителей снова работают", "Viewer effects are back on"));
        }

        int IntField(int v, float width)
        {
            var s = GUILayout.TextField(v.ToString(), 6, GUILayout.Width(width));
            return int.TryParse(s, out var n) && n >= 0 ? n : s.Length == 0 ? 0 : v;
        }

        bool EggToggleW(bool value, string text, float width, string tip = null)
        {
            bool clicked = GUILayout.Button(new GUIContent(text, tip), sToggle, GUILayout.Width(width));
            var r = GUILayoutUtility.GetLastRect();
            var icon = value ? eggIcon : eggOff;
            if (icon != null) GUI.DrawTexture(new Rect(r.x + 4, r.y + (r.height - 18) / 2, 18, 18), icon, ScaleMode.ScaleToFit);
            return clicked ? !value : value;
        }
    }
}
