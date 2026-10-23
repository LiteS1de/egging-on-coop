using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace EggCoop
{
    public class CoopConfig
    {
        public ConfigEntry<string> Nick, SignalUrl, StunServers, LastJoin, TpStats;
        public ConfigEntry<int> Port, GhostDelayMs;
        public ConfigEntry<KeyCode> MenuKey, TpKey;
        public ConfigEntry<bool> Collide, CrackOnHit;
    }

    [BepInPlugin("eggcoop", "Egging On Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.4.1";
        public static ManualLogSource Log;
        static Plugin instance;
        readonly HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("eggcoop");
        WorldSync world;
        float inGameSince = -1f;
        bool wasActive;
        const float SendInterval = 1f / 30f, GhostTimeout = 5f;

        readonly CoopConfig cfg = new CoopConfig();
        readonly Dictionary<int, Ghost> ghosts = new Dictionary<int, Ghost>();
        readonly Dictionary<int, EggState> pending = new Dictionary<int, EggState>();
        CoopNet net;
        bool menuOpen;
        float nextSend;
        Rect window = new Rect(40, 40, 500, 0);
        string joinText = "";
        GUIStyle tag;

        void Awake()
        {
            Log = Logger;
            instance = this;
            // Show the teleport summary when the game's end credits start.
            try { harmony.Patch(HarmonyLib.AccessTools.Method(typeof(EndCredits), nameof(EndCredits.Play)), postfix: new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(CreditsPostfix))); }
            catch (System.Exception e) { Log.LogWarning("Credits hook failed: " + e.Message); }
            cfg.Nick = Config.Bind("General", "Nick", "Egg" + Random.Range(100, 999), "Имя над вашим яйцом");
            cfg.MenuKey = Config.Bind("General", "MenuKey", KeyCode.F9, "Клавиша окна кооператива");
            cfg.TpKey = Config.Bind("General", "TeleportKey", KeyCode.T, "Телепорт к другу (при 2+ друзьях — выбор цифрой)");
            cfg.TpStats = Config.Bind("Stats", "Teleports", "", "Сколько раз вы телепортировались к каждому (ник:число;...)");
            foreach (var part in cfg.TpStats.Value.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.LastIndexOf(':');
                if (c > 0 && int.TryParse(part.Substring(c + 1), out var n)) tpCounts[part.Substring(0, c)] = n;
            }
            cfg.Port = Config.Bind("Network", "Port", 7788, "UDP-порт хоста");
            cfg.SignalUrl = Config.Bind("Network", "SignalUrl", "https://ntfy.sh", "ntfy-сервер для пробивки NAT");
            cfg.StunServers = Config.Bind("Network", "StunServers", "stun.l.google.com:19302,stun.cloudflare.com:3478", "STUN-серверы через запятую");
            cfg.LastJoin = Config.Bind("Network", "LastJoin", "", "Последний код или IP");
            cfg.GhostDelayMs = Config.Bind("Network", "GhostDelayMs", 100, "Насколько призраки отстают ради плавности (мс). Больше — плавнее на плохой сети");
            Ghost.Delay = cfg.GhostDelayMs.Value / 1000f;
            cfg.Collide = Config.Bind("Host", "Collide", true, "Яйца сталкиваются (false — проходят друг сквозь друга). Решает хост");
            cfg.CrackOnHit = Config.Bind("Host", "CrackOnHit", false, "Удар о друга может разбить скорлупу. Решает хост");
            joinText = cfg.LastJoin.Value;
            net = new CoopNet(this, cfg);
            net.OnState += (id, s) => pending[id] = s;
            net.OnLeave += RemoveGhost;
            net.OnSettings += ApplyHostSettings;
            net.OnEnded += Toast;
            try { world = new WorldSync(net, harmony); } catch (System.Exception e) { Log.LogError("World sync disabled: " + e); }
            Application.quitting += () => net.Stop();   // tell friends right away instead of letting them time out
            ApplyOwnSettings();
            ReadTestArgs();
            if (dataDir != null) IsolateData();
            // Two full copies of the game need ~15 GB together; smaller textures keep the lab inside a 16 GB PC.
            if (lab) { QualitySettings.globalTextureMipmapLimit = 3; Application.targetFrameRate = 30; }
            Log.LogInfo($"Egging On Co-op loaded. {cfg.MenuKey.Value} opens the menu.");
        }

        // Test harness: "-coop-nick A -coop-host" / "-coop-join 127.0.0.1:7788" start the session on its own,
        // "-coop-shot 10" saves an in-game screenshot every 10 s to BepInEx/coop-shots.
        string testMode, testJoin;
        float shotEvery, nextShot, nextProbe;
        void ReadTestArgs()
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string next = i + 1 < a.Length ? a[i + 1] : "";
                if (a[i] == "-coop-host") testMode = "host";
                else if (a[i] == "-coop-join") { testMode = "join"; testJoin = next; }
                else if (a[i] == "-coop-nick") cfg.Nick.Value = next;
                else if (a[i] == "-coop-shot") float.TryParse(next, out shotEvery);
                else if (a[i] == "-coop-lab") lab = true;
                else if (a[i] == "-coop-data") dataDir = next;
                else if (a[i] == "-coop-collide") cfg.Collide.Value = next == "1";
                else if (a[i] == "-coop-crack") cfg.CrackOnHit.Value = next == "1";
#if LAB
                else if (a[i] == "-coop-noshove") noShove = true;
                else if (a[i] == "-coop-standtest") { standTest = true; PatchCrackLog(); }
                else if (a[i] == "-coop-hitlog") PatchCrackLog();   // log friend hits and what caused each crack
                else if (a[i] == "-coop-tptest") tpTest = true;
                else if (a[i] == "-coop-menu") openMenu = true;
                else if (a[i] == "-coop-worlddump") worldDump = true;
                else if (a[i] == "-coop-uidump") uiDump = true;
                else if (a[i] == "-coop-uidemo") uiDemo = uiDemoPicker = true;
                else if (a[i] == "-coop-pushtest") pushTest = true;   // guest throws the nearest free object every 12 s
                else if (a[i] == "-coop-worldlog") worldLog = true;   // log the pushed object + a platform every 2 s   // F9 open on arrival, for screenshots
                else if (a[i] == "-coop-crackfirst") crackFirst = true;   // join right after our shell rebuilds: the ghost is built then
#endif
            }
        }

        // "-coop-data <dir>": a test copy keeps saves, settings and controls in <dir> instead of the player's
        // real LocalLow folder. Fails closed: a copy that cannot be isolated quits before the game touches files.
        static string dataDir;
        void IsolateData()
        {
            try
            {
                System.IO.Directory.CreateDirectory(dataDir);
                new HarmonyLib.Harmony("eggcoop.lab").Patch(
                    HarmonyLib.AccessTools.PropertyGetter(typeof(Application), nameof(Application.persistentDataPath)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(DataPathPostfix)));
            }
            catch (System.Exception e) { Log.LogError("Isolation patch failed: " + e); }
            if (Application.persistentDataPath != dataDir)
            {
                Log.LogFatal($"Data isolation failed ({Application.persistentDataPath}); quitting to protect real saves");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            Log.LogInfo("Lab data dir: " + dataDir);
        }

        static void DataPathPostfix(ref string __result) => __result = dataDir;

        // "-coop-lab": keep the copy in a small window and press Continue on the title screen.
        bool lab, crackedOnce;
        float nextLabStep, lastInput = -100f;
        void LabStep(bool inGame)
        {
            if (!lab) return;
            if (inGame && World.instance.GetEgg().IsInput()) lastInput = Time.unscaledTime;
            if (Time.unscaledTime < nextLabStep) return;
            nextLabStep = Time.unscaledTime + 3f;
            // tools/net_sweep.py changes the ghost delay live through this file.
            var delayFile = System.IO.Path.Combine(Paths.BepInExRootPath, "coop-delay.txt");
            if (System.IO.File.Exists(delayFile) && int.TryParse(System.IO.File.ReadAllText(delayFile).Trim(), out var ms) && ms / 1000f != Ghost.Delay)
            {
                Ghost.Delay = ms / 1000f;
                Log.LogInfo($"Lab: ghost delay {ms} ms");
            }
            if (!lowApplied && inGame) ApplyLowPreset();
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != 960)
                Screen.SetResolution(960, 540, FullScreenMode.Windowed);
            var title = inGame ? null : FindObjectOfType<TitleScreen>();
            // Outside "dev" the title waits for "press any key"; marking the menu initialized is what that key press leads to.
            if (title != null && !TitleScreen.menuInitialized) { Log.LogInfo("Lab: skip press-any-key"); TitleScreen.menuInitialized = true; }
            else if (title != null) { Log.LogInfo("Lab: Continue"); title.Continue(); nextLabStep += 10f; }
#if LAB
            // The host's egg gets a shove now and then, so the guest has a moving target to follow,
            // but only while nobody plays it: a person at the keyboard wins.
            // Lab builds only (-c Lab): a release must never move an egg the player left alone.
            if (inGame && net.IsHost && !noShove && Time.unscaledTime - lastInput > 15f && !World.instance.GetEggCore().yolkState)
            {
                World.instance.GetEggCore().eggRigidbody.AddForce(Quaternion.Euler(0, Random.Range(0, 360), 0) * new Vector3(0, 3f, 4f), ForceMode.VelocityChange);
                // The game autosaves only once the player has pressed a movement key; the shove stands in for that.
                if (!Persistent.instance.saveData.eggMoved) { Persistent.instance.saveData.eggMoved = true; Persistent.instance.ConstantAutoSave(); }
            }
            // The game pauses itself after 5 min with no buttons pressed (EggController.afkTimeout); long lab runs must not.
            if (inGame) HarmonyLib.AccessTools.Field(typeof(EggController), "afkTimeout").SetValue(World.instance.GetEgg(), 0f);
            if (inGame && openMenu && net.Active) { openMenu = false; SetMenu(true); }
            if (inGame && worldDump && Time.unscaledTime > 40f) { worldDump = false; DumpWorld(); }
            if (inGame) WorldLabStep();
            if (inGame && uiDump && Time.unscaledTime > 30f) { uiDump = false; DumpUi(); }
            if (uiDemo && toasts.Count == 0) Toast("Телепорт к Host — так выглядит оповещение");
            if (inGame && standTest && !net.IsHost) StandTestStep();
            if (inGame && tpTest && !net.IsHost && ghosts.Count > 0) { var g = ghosts.Values.First(); Log.LogInfo($"Lab tp: {TryTeleport(g)}"); nextLabStep += 9f; }
#endif
        }

#if LAB
        // "-coop-standtest" (guest): drop onto the host's ghost from 1.5 m, then from 40 m, and log where we ended up
        // and whether the shell cracked. Read with: Select-String LogOutput.log "Lab stand"
        bool noShove, standTest, tpTest, crackFirst, openMenu, worldDump, pushTest, worldLog, uiDump, uiDemo;
        static int labBody, pushCount;
        float nextPush, nextWorldLog;
        void WorldLabStep()
        {
            float now = Time.unscaledTime;
            if (pushTest && !net.IsHost && net.Connected && now > nextPush && world != null && world.Count > 0)
            {
                nextPush = now + 12f;
                if (++pushCount % 2 == 1) { labBody = world.TestPush(World.instance.GetEgg().GetPosition()); Log.LogInfo($"Lab push: {labBody} {world.Describe(labBody)}"); }
                else
                {
                    // Real path: drop our egg on the object; the collision must claim it.
                    labBody = world.Nearest(World.instance.GetEgg().GetPosition(), out var bp);
                    Place(bp + Vector3.up * 1.5f);
                    Log.LogInfo($"Lab drop on: {labBody} {world.Describe(labBody)}");
                }
            }
            if (worldLog && now > nextWorldLog && world != null)
            {
                nextWorldLog = now + 2f;
                var mover = Resources.FindObjectsOfTypeAll<KinematicCurveMovement>().Where(m => m.gameObject.scene.IsValid()).OrderBy(m => m.name).FirstOrDefault();
                var mp = mover != null ? mover.transform.position : Vector3.zero;
                Log.LogInfo(System.FormattableString.Invariant($"Lab world t={System.DateTimeOffset.Now.ToUnixTimeMilliseconds()} clock={SharedClock.Time:F2} off={SharedClock.Offset:F2} mover=({mp.x:F2},{mp.y:F2},{mp.z:F2}) body={(labBody != 0 ? labBody : world.LastRemoteBody)} {world.Describe(labBody != 0 ? labBody : world.LastRemoteBody)}"));
            }
        }

        // UI recon: the game's fonts, the sprites its menus draw with (saved as PNG) and its text colours.
        void DumpUi()
        {
            var dir = System.IO.Path.Combine(Paths.BepInExRootPath, "ui-dump");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) Log.LogInfo($"UI font: {f.name} dynamic={f.dynamic} names={string.Join(",", f.fontNames ?? new string[0])}");
            foreach (var f in Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>()) Log.LogInfo($"UI tmp: {f.name} source={(f.sourceFontFile != null ? f.sourceFontFile.name : "null")} atlas={f.atlasTexture?.width}");
            var seen = new HashSet<Sprite>();
            foreach (var img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
            {
                var sp = img.sprite;
                if (sp == null || !seen.Add(sp) || seen.Count > 80) continue;
                Log.LogInfo($"UI sprite: {sp.name} {sp.rect.width}x{sp.rect.height} border={sp.border} on {img.name} color={img.color} type={img.type}");
                try
                {
                    var rt = RenderTexture.GetTemporary(sp.texture.width, sp.texture.height, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(sp.texture, rt);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var r = sp.textureRect;
                    var tex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0); tex.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, sp.name.Replace('/', '_') + ".png"), tex.EncodeToPNG());
                }
                catch (System.Exception e) { Log.LogInfo($"UI sprite {sp.name}: {e.Message}"); }
            }
            foreach (var t in Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>().Where(t => t.gameObject.scene.IsValid()).Take(60))
                Log.LogInfo($"UI text: '{t.text?.Replace("\n", " ")}' font={t.font?.name} size={t.fontSize} color={t.color} style={t.fontStyle} on {t.transform.parent?.name}/{t.name}");
        }

        // Stage 4 recon: what moves in this world and how the game identifies it.
        void DumpWorld()
        {
            var holders = Resources.FindObjectsOfTypeAll<RigidDataHolder>().Where(h => h.gameObject.scene.IsValid()).ToList();
            Log.LogInfo($"World: {holders.Count} RigidDataHolders, {holders.Select(h => h.ID).Distinct().Count()} distinct IDs, {holders.Count(h => h.ID < 0)} without ID");
            foreach (var grp in holders.GroupBy(h => h.GetType().Name).OrderByDescending(g => g.Count())) Log.LogInfo($"World:   {grp.Key} x{grp.Count()}");
            var held = new HashSet<Rigidbody>(holders.Select(h => h.rb));
            var loose = Resources.FindObjectsOfTypeAll<Rigidbody>().Where(r => r.gameObject.scene.IsValid() && !r.isKinematic && !held.Contains(r)).ToList();
            Log.LogInfo($"World: {loose.Count} other dynamic rigidbodies");
            foreach (var grp in loose.GroupBy(r => string.Join("+", r.GetComponents<MonoBehaviour>().Where(m => m != null).Select(m => m.GetType().Name).DefaultIfEmpty("(none)"))).OrderByDescending(g => g.Count()).Take(25))
                Log.LogInfo($"World:   {grp.Key} x{grp.Count()}  e.g. {grp.First().name}");
            var movers = Resources.FindObjectsOfTypeAll<KinematicCurveMovement>().Count(m => m.gameObject.scene.IsValid());
            Log.LogInfo($"World: {movers} KinematicCurveMovement, {Resources.FindObjectsOfTypeAll<OneTimeEvent>().Count(m => m.gameObject.scene.IsValid())} OneTimeEvent");
        }

        // Which collider the egg hit last, so a crack can be blamed on the friend or on the floor.
        static string lastHit = "-";
        static float lastHitSpeed;
        static void HitPrefix(Collision collision)
        {
            lastHit = collision.collider.name + (collision.collider.transform.parent?.name.StartsWith("EggCoop_Ghost") == true ? "(friend)" : "");
            lastHitSpeed = collision.relativeVelocity.magnitude;
            if (lastHit.EndsWith("(friend)")) Log.LogInfo(System.FormattableString.Invariant($"Lab hit friend at {lastHitSpeed:0.0} m/s"));
        }
        static void CrackPrefix(MethodBase __originalMethod) =>
            Log.LogInfo(System.FormattableString.Invariant($"Lab crack: {__originalMethod.Name} after hitting {lastHit} at {lastHitSpeed:0.0} m/s"));
        void PatchCrackLog()
        {
            var h = new HarmonyLib.Harmony("eggcoop.lab.crack");
            h.Patch(HarmonyLib.AccessTools.Method(typeof(EggController), nameof(EggController.OnCollisionEnterEvent)), new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(HitPrefix)));
            foreach (var m in new[] { nameof(EggController.FullCrack), nameof(EggController.SemiCrack) })
                h.Patch(HarmonyLib.AccessTools.Method(typeof(EggController), m), new HarmonyLib.HarmonyMethod(typeof(Plugin), nameof(CrackPrefix)));
        }
        int standPhase, cracksBefore;
        float dropHeight;
        void StandTestStep()
        {
            var g = ghosts.Values.FirstOrDefault();
            if (g == null) return;
            var core = World.instance.GetEggCore();
            if (standPhase % 2 == 0)
            {
                if (core.yolkState) return;   // wait for the shell to rebuild before the next drop
                cracksBefore = Persistent.instance.saveData.crackCount;
                if (standPhase % 4 == 0) { dropHeight = 1.5f; Place(g.Position + Vector3.up * 1.5f); }   // land on the friend's head
                else
                {
                    // Slam: 2.5 m above the friend, 22 m/s straight down (enough to crack on a floor).
                    // Not a long drop (the start area has a roof) and not sideways (the henhouse planks get hit first).
                    dropHeight = 0f;
                    Place(g.Position + Vector3.up * 2.5f);
                    World.instance.GetEgg().SetVelocities(Vector3.down * 22f, Vector3.zero);
                }
                nextLabStep += 5f;   // land and settle
            }
            else
            {
                float dy = World.instance.GetEgg().GetPosition().y - g.Position.y;
                bool cracked = Persistent.instance.saveData.crackCount != cracksBefore || core.yolkState;
                Log.LogInfo(System.FormattableString.Invariant(
                    $"Lab stand: {(dropHeight > 0 ? "drop 1.5 m" : "slam 22 m/s")} collide={Ghost.Collide} crackOnHit={Ghost.CrackOnHit} dy={dy:0.00} horiz={Vector3.Distance(World.instance.GetEgg().GetPosition() - Vector3.up * dy, g.Position):0.00} cracked={cracked}"));
                nextLabStep += 5f;
            }
            standPhase++;
        }
#endif

        // Lab copies use the game's own Steam Deck "low" preset (Persistent.lowDefaultsOverrides): RAM for two copies.
        // Options live in the lab's isolated settings.prefs, never the player's.
        bool lowApplied;
        void ApplyLowPreset()
        {
            lowApplied = true;
            var low = HarmonyLib.AccessTools.Field(typeof(Persistent), "lowDefaultsOverrides")?.GetValue(Persistent.instance) as System.Collections.IEnumerable;
            if (low == null) { Log.LogWarning("Lab: no low preset found"); return; }
            var applied = new List<string>();
            foreach (var kv in low)
            {
                var t = kv.GetType();
                var key = (OptionType)t.GetProperty("Key").GetValue(kv);
                var value = (int)t.GetProperty("Value").GetValue(kv);
                if (Persistent.instance.GetOptionValue(key) == value) continue;
                try { Persistent.instance.SetOptionValue(key, value); } catch (System.Exception) { }   // stored anyway; applied on next start
                applied.Add($"{key}={value}");
            }
            // Two copies at full volume at once are loud; owner asked for 30 %.
            if (Persistent.instance.GetOptionValue(OptionType.MasterVolume) != 30) { Persistent.instance.SetOptionValue(OptionType.MasterVolume, 30); applied.Add("MasterVolume=30"); }
            Log.LogInfo("Lab: low preset " + (applied.Count > 0 ? string.Join(" ", applied) : "already set"));
        }

        void RunTestArgs(bool inGame)
        {
            LabStep(inGame);
#if LAB
            if (testMode != null && inGame && crackFirst)
            {
                var core = World.instance.GetEggCore();
                if (!core.yolkState && !crackedOnce) { crackedOnce = true; World.instance.GetEgg().FullCrack(core.eggRigidbody.position, Vector3.right); Log.LogInfo("Lab: cracked before joining"); }
                if (core.yolkState || !crackedOnce) return;
            }
#endif
            if (testMode != null && inGame)
            {
                Log.LogInfo("Test harness: " + testMode);
                if (testMode == "host") { ApplyOwnSettings(); net.Host(); } else net.Join(testJoin);
                testMode = null;
            }
            // Probe for tools/compare_probe.py: wall-clock ms, own egg, every ghost, 10 times a second.
            if (lab && inGame && Time.unscaledTime >= nextProbe)
            {
                nextProbe = Time.unscaledTime + 0.1f;
                var p = World.instance.GetEgg().GetPosition();
                System.IO.File.AppendAllText(System.IO.Path.Combine(Paths.BepInExRootPath, "coop-probe.txt"),
                    System.FormattableString.Invariant($"{System.DateTimeOffset.Now.ToUnixTimeMilliseconds()} me {p.x:F3} {p.y:F3} {p.z:F3}") +
                    string.Concat(ghosts.Values.Select(g => System.FormattableString.Invariant($" {g.Nick} {g.Position.x:F3} {g.Position.y:F3} {g.Position.z:F3}"))) + "\n");
            }
            if (shotEvery > 0 && Time.unscaledTime >= nextShot)
            {
                nextShot = Time.unscaledTime + shotEvery;
                var dir = System.IO.Path.Combine(Paths.BepInExRootPath, "coop-shots");
                System.IO.Directory.CreateDirectory(dir);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"{Time.unscaledTime:0000}.png"));
                Log.LogInfo($"t={System.DateTime.Now:HH:mm:ss.fff} me={(inGame ? World.instance.GetEgg().GetPosition().ToString() : "-")} inGame={inGame} net={net.Active} players={(net.Active ? net.PlayerCount : 0)} ghosts=" +
                    string.Join(",", ghosts.Values.Select(g => $"{g.Nick}@{g.Position}")));
            }
        }

        // ---------------------------------------------------------------- frame loop

        readonly Dictionary<string, int> tpCounts = new Dictionary<string, int>();
        byte tpSeq;
        string waitKey;           // "menu" / "tp" while F9 waits for a new key
        int ignoreKeysFrame = -1;
        float pickerUntil, summaryUntil;
        bool showLog;
        List<string> hostAddrs;
        readonly List<(string text, float until)> toasts = new List<(string, float)>();
        GUIStyle sWin, sHead, sText, sDim, sBtn, sToast, sPanel, sToggle;
        Vector2 menuScroll;
        float menuContentHeight = 600;   // measured each repaint; the scroll area is this tall, capped by the screen

        static bool InGame()
        {
            var w = World.instance;
            if (w == null || !World.isPlaying || w.GetEgg() == null || w.GetEggCore() == null) return false;
            var core = w.GetEggCore();
            return core.gameObject.activeInHierarchy || core.yolkState;   // a cracked egg is still playing: its body is hidden while the yolk rolls
        }

        void Update()
        {
            Application.runInBackground = true;   // the host must keep sending while alt-tabbed
            if (waitKey == null && Time.frameCount > ignoreKeysFrame) HandleKeys();
            net.Poll();

            bool inGame = InGame();
            // Register the world's objects once the level has settled (stabilizers unfreeze bodies ~0.5 s after spawn).
            inGameSince = inGame ? (inGameSince < 0 ? Time.unscaledTime : inGameSince) : -1f;
            if (world != null && inGameSince >= 0 && Time.unscaledTime - inGameSince > 3f) world.Register();
            if (wasActive && !net.Active) world?.Reset();
            wasActive = net.Active;
            world?.Update();
            RunTestArgs(inGame);
            if (inGame && net.Active && Time.unscaledTime >= nextSend)
            {
                nextSend = Time.unscaledTime + SendInterval;
                net.SendLocal(LocalState());
            }

            foreach (var kv in pending.ToList())
            {
                if (!ghosts.TryGetValue(kv.Key, out var g))
                {
                    if (!Ghost.CanBuild) continue;   // need our shell (not mid-crack) to copy the look from
                    ghosts[kv.Key] = g = new Ghost(kv.Key);
                    Toast($"{kv.Value.nick} в игре");
                }
#if LAB
                if (g.TpTotal != 0 && kv.Value.tpTotal != g.TpTotal) WatchPeak($"tp victim of {g.Nick}");
#endif
                g.Push(kv.Value);
                pending.Remove(kv.Key);
            }
            foreach (var kv in ghosts.ToList())
            {
                if (Time.unscaledTime - kv.Value.LastSeen > GhostTimeout) { RemoveGhost(kv.Key); continue; }
                kv.Value.Update(inGame);
            }
            if (pickerUntil > 0 && (Time.unscaledTime > pickerUntil || ghosts.Count < 2)) pickerUntil = 0;
            if (net.Active && net.IsHost && hostAddrs == null) hostAddrs = LocalAddresses(cfg.Port.Value);
            if (!net.Active) hostAddrs = null;
#if LAB
            MeasurePeak();
#endif
        }

        void FixedUpdate() => world?.FixedUpdate();

        void HandleKeys()
        {
            if (Input.GetKeyDown(cfg.MenuKey.Value)) SetMenu(!menuOpen);
            if (menuOpen) return;
            if (Input.GetKeyDown(cfg.TpKey.Value))
            {
                var others = Others();
                if (others.Count == 0) Toast(net.Active ? "Друзья ещё не появились" : "Вы не в кооперативе: F9 → создать игру или подключиться");
                else if (others.Count == 1) TryTeleport(others[0]);
                else pickerUntil = pickerUntil > 0 ? 0 : Time.unscaledTime + 5f;
                return;
            }
            if (pickerUntil <= 0) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { pickerUntil = 0; return; }
            var list = Others();
            for (int n = 0; n < list.Count && n < 9; n++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + n) || Input.GetKeyDown(KeyCode.Keypad1 + n)) { pickerUntil = 0; TryTeleport(list[n]); return; }
        }

        List<Ghost> Others() => ghosts.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        EggState LocalState()
        {
            var egg = World.instance.GetEgg();
            var core = World.instance.GetEggCore();
            return new EggState
            {
                pos = egg.GetPosition(),
                rot = core.yolkState ? egg.GetRawTransform().rotation : core.eggRigidbody.rotation,
                yolk = core.yolkState,
                skin = Persistent.instance.saveData.currentSkinID,
                nick = cfg.Nick.Value,
                tpSeq = tpSeq,
                tpTotal = (ushort)Mathf.Min(tpCounts.Values.Sum(), ushort.MaxValue),
            };
        }

        void RemoveGhost(int id)
        {
            pending.Remove(id);
            if (ghosts.TryGetValue(id, out var g)) { Toast($"{g.Nick} вышел"); g.Destroy(); ghosts.Remove(id); }
        }

        void ApplyHostSettings(bool collide, bool crack)
        {
            if (collide != Ghost.Collide || crack != Ghost.CrackOnHit)
                Toast("Хост: " + (collide ? "столкновения вкл" + (crack ? ", удар разбивает" : "") : "режим призраков"));
            Ghost.Collide = collide; Ghost.CrackOnHit = crack;
        }

        // ---------------------------------------------------------------- teleport

        // Returns what happened, also shown as a toast.
        string TryTeleport(Ghost g)
        {
            string msg = !InGame() ? "Телепорт работает только в игре"
                : !g.Fresh ? $"{g.Nick}: нет связи — телепорт невозможен"
                : World.instance.GetEggCore().freeze ? "Ваше яйцо ещё не готово"
                : null;
            if (msg == null)
            {
                // Land on the friend's spot with collisions paused on both sides (tpSeq tells them): dropping onto a
                // lying friend used to launch one of you. Falling friends are fine too: you get their current spot.
                Place(g.Position + Vector3.up);
                tpSeq++;
                foreach (var o in ghosts.Values) o.PauseCollisions();
                tpCounts[g.Nick] = (tpCounts.TryGetValue(g.Nick, out var n) ? n : 0) + 1;
                cfg.TpStats.Value = string.Join(";", tpCounts.Select(kv => $"{kv.Key}:{kv.Value}"));
                msg = g.Yolk ? $"Телепорт к {g.Nick} — он разбит, вы у его желтка" : $"Телепорт к {g.Nick}";
#if LAB
                WatchPeak("tp self");
#endif
            }
            Toast(msg);
            return msg;
        }

        // Same moves as the game's own "tp" console command.
        static void Place(Vector3 pos)
        {
            var egg = World.instance.GetEgg();
            var core = World.instance.GetEggCore();
            if (!core.yolkState) { core.transform.SetPositionAndRotation(pos, Quaternion.identity); egg.SetVelocities(Vector3.zero, Vector3.zero); }
            else egg.GetRawTransform().SetPositionAndRotation(pos, Quaternion.identity);
            World.instance.GetProgress().Teleport(pos.y);
        }

#if LAB
        // Peak speed of our own egg in the 4 s after a teleport: a kick shows as 8+ m/s, a plain 1 m drop as ~4.
        float peak, peakUntil; string peakLabel;
        void WatchPeak(string label) { peak = 0; peakUntil = Time.unscaledTime + 6f; peakLabel = label; }
        void MeasurePeak()
        {
            if (peakUntil <= 0) return;
            if (InGame()) peak = Mathf.Max(peak, World.instance.GetEgg().GetVelocity().magnitude);
            if (Time.unscaledTime < peakUntil) return;
            var near = InGame() && ghosts.Count > 0 ? Vector3.Distance(World.instance.GetEgg().GetPosition(), ghosts.Values.First().Position) : -1f;
            Log.LogInfo(System.FormattableString.Invariant($"Lab peak: {peakLabel} {peak:0.0} m/s, friend {near:0.00} m away"));
            peakUntil = 0;
        }
#endif

        static void CreditsPostfix() { if (instance != null) instance.summaryUntil = Time.unscaledTime + 120f; }

        // ---------------------------------------------------------------- settings

        // Our own config rules while we host or play alone; a host's choice replaces it while we are a guest.
        void ApplyOwnSettings()
        {
            Ghost.Collide = cfg.Collide.Value;
            Ghost.CrackOnHit = cfg.CrackOnHit.Value;
            net.SetSettings(Ghost.Collide, Ghost.CrackOnHit);
        }

        void SetMenu(bool open)
        {
            menuOpen = open;
            waitKey = null;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            // Typing a nick must not roll the egg, and moving the mouse over the menu must not turn the camera.
            try { Rewired.ReInput.controllers.Keyboard.enabled = !open; Rewired.ReInput.controllers.Mouse.enabled = !open; } catch { }
        }

        void Toast(string text)
        {
            Log.LogInfo("Toast: " + text);
            toasts.Add((text, Time.unscaledTime + 4f));
            if (toasts.Count > 4) toasts.RemoveAt(0);
        }

        // Addresses friends can type for a direct join: Radmin, Hamachi, ZeroTier, Tailscale, home network.
        static List<string> LocalAddresses(int port)
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        || ni.Name.StartsWith("vEthernet")) continue;   // Hyper-V/WSL switches: nobody can reach them
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && Reachable(ua.Address.GetAddressBytes()))
                            list.Add($"{ua.Address}:{port}|{ni.Name}");
                }
            }
            catch (System.Exception e) { Log.LogWarning("Interfaces: " + e.Message); }
            return list.OrderBy(a => a.StartsWith("26.") ? 0 : 1).ToList();   // Radmin first: what friends used
        }

        // Not link-local (169.254) and not a VPN client's fake-IP tunnel (198.18.0.0/15).
        static bool Reachable(byte[] b) => b[0] != 169 && !(b[0] == 198 && (b[1] == 18 || b[1] == 19));

        // ---------------------------------------------------------------- GUI

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        // A tinted, readable copy of one of the game's own UI sprites (its textures are not CPU-readable).
        static Texture2D GameSprite(string name, Color tint, out RectOffset border)
        {
            border = new RectOffset();
            var sp = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(x => x.name == name);
            if (sp == null) return null;
            var rt = RenderTexture.GetTemporary(sp.texture.width, sp.texture.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(sp.texture, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var r = sp.textureRect;
            var tex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.ReadPixels(new Rect(r.x, r.y, r.width, r.height), 0, 0);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i] *= tint;
            tex.SetPixels(px); tex.Apply();
            var bd = sp.border;   // left, bottom, right, top
            border = new RectOffset((int)bd.x, (int)bd.z, (int)bd.w, (int)bd.y);
            return tex;
        }

        // The game's look: its Arvo font, white text, grey secondary text, orange (#FFA727) accents, its rounded
        // "UIBevel" panels and the little egg it uses as a marker. Falls back to flat colours if an asset is missing.
        static readonly Color Orange = new Color(1f, 0.655f, 0.153f), Grey = new Color(0.75f, 0.75f, 0.75f);
        Texture2D eggIcon, eggOff;
        bool uiDemoPicker;   // lab screenshots only

        void InitStyles()
        {
            if (sWin != null) return;
            var font = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f => f.name == "Arvo-Regular");
            if (font == null && Time.unscaledTime < 20f) { BasicStyles(); return; }   // game UI not loaded yet: try again next frame
            var panel = GameSprite("UIBevel", new Color(0f, 0f, 0f, 0.8f), out var panelBorder) ?? Tex(new Color(0f, 0f, 0f, 0.8f));
            var hover = GameSprite("UIBevel", new Color(1f, 0.655f, 0.153f, 0.22f), out var hoverBorder) ?? Tex(new Color(1f, 0.655f, 0.153f, 0.22f));
            var field = GameSprite("UIBevel", new Color(1f, 1f, 1f, 0.08f), out var fieldBorder) ?? Tex(new Color(1f, 1f, 1f, 0.08f));
            eggIcon = GameSprite("smallEgg", Orange, out _);
            eggOff = GameSprite("smallEgg", new Color(1f, 1f, 1f, 0.3f), out _);
            // The bevel's corners are 45 px of a 256 px sprite: draw them at half size so small widgets stay round, not blobby.
            RectOffset Half(RectOffset o) => new RectOffset(o.left / 2, o.right / 2, o.top / 2, o.bottom / 2);

            GUIStyle Label(int size, Color c) { var st = new GUIStyle(GUI.skin.label) { font = font, fontSize = size, wordWrap = true, richText = true }; st.normal.textColor = c; return st; }
            sWin = new GUIStyle(GUI.skin.window) { font = font, padding = new RectOffset(18, 18, 40, 14), fontSize = 20, alignment = TextAnchor.UpperCenter, border = panelBorder };
            sWin.normal.background = sWin.onNormal.background = sWin.focused.background = sWin.onFocused.background = panel;
            sWin.normal.textColor = sWin.onNormal.textColor = sWin.focused.textColor = sWin.onFocused.textColor = Orange;
            sHead = Label(15, Orange); sHead.margin = new RectOffset(0, 0, 14, 4);
            sText = Label(14, Color.white);
            sDim = Label(12, Grey);
            sBtn = new GUIStyle(GUI.skin.button) { font = font, fontSize = 14, fixedHeight = 28, border = Half(hoverBorder), padding = new RectOffset(10, 10, 4, 4) };
            sBtn.normal.background = field; sBtn.hover.background = sBtn.active.background = hover;
            sBtn.normal.textColor = Color.white; sBtn.hover.textColor = sBtn.active.textColor = Orange;
            // Checkboxes are text buttons with the game's egg drawn in front: orange egg = on, faint egg = off.
            sToggle = Label(14, Color.white); sToggle.padding = new RectOffset(30, 4, 4, 4); sToggle.wordWrap = false;
            sToggle.hover.textColor = sToggle.active.textColor = Orange;
            tag = new GUIStyle(GUI.skin.label) { font = font, alignment = TextAnchor.MiddleCenter, fontSize = 17 };
            sToast = new GUIStyle(GUI.skin.box) { font = font, fontSize = 16, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(40, 18, 9, 9), border = Half(panelBorder), richText = true };
            sToast.normal.background = panel; sToast.normal.textColor = Color.white;
            sPanel = new GUIStyle(sToast) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(20, 20, 14, 14), fontSize = 16 };
            GUI.skin.textField.font = font; GUI.skin.textField.fontSize = 14;
            GUI.skin.textField.normal.background = GUI.skin.textField.hover.background = GUI.skin.textField.focused.background = field;
            GUI.skin.textField.border = Half(fieldBorder); GUI.skin.textField.padding = new RectOffset(8, 8, 5, 5);
            GUI.skin.textField.normal.textColor = GUI.skin.textField.focused.textColor = GUI.skin.textField.hover.textColor = Color.white;
            Log.LogInfo($"UI styled: font={(font != null ? font.name : "default")} panel={(panel.width > 1)} egg={(eggIcon != null)}");
        }

        // Plain styles for the first seconds, before the game has loaded its UI assets.
        void BasicStyles()
        {
            if (sText != null) return;
            sText = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            sDim = sHead = sText; sBtn = GUI.skin.button; sToggle = GUI.skin.toggle; sWin = null;
            tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
            sToast = sPanel = new GUIStyle(GUI.skin.box) { fontSize = 14, richText = true };
        }
        void OnGUI()
        {
            InitStyles();
            var e = Event.current;
            if (waitKey != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode != KeyCode.Escape) (waitKey == "menu" ? cfg.MenuKey : cfg.TpKey).Value = e.keyCode;
                waitKey = null;
                ignoreKeysFrame = Time.frameCount + 1;   // the same press must not also open the menu or teleport
                e.Use();
            }
            DrawNameTags();
            DrawToasts();
            if (pickerUntil > 0 || (uiDemoPicker && ghosts.Count > 0)) DrawPicker();
            if (summaryUntil > Time.unscaledTime) DrawSummary(new Rect(40, 40, 360, 0));
            if (menuOpen)
            {
                window.width = Mathf.Min(540, Screen.width - 20); window.height = 0;   // let the layout pick the height every frame
                window = GUILayout.Window(0x6E99, window, DrawMenu, $"EGGING ON CO-OP  {Version}", sWin ?? GUI.skin.window);
                // Keep it on screen: small windowed games are shorter than the menu (it scrolls inside).
                window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
                window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            }
        }

        void DrawToasts()
        {
            toasts.RemoveAll(t => t.until < Time.unscaledTime);
            float y = 60;
            foreach (var t in toasts)
            {
                var size = sToast.CalcSize(new GUIContent(t.text));
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01((t.until - Time.unscaledTime) * 2f));
                var box = new Rect((Screen.width - size.x) / 2, y, size.x, size.y);
                GUI.Box(box, t.text, sToast);
                if (eggIcon != null) GUI.DrawTexture(new Rect(box.x + 14, box.y + (box.height - 18) / 2, 18, 18), eggIcon, ScaleMode.ScaleToFit);
                y += size.y + 6;
            }
            GUI.color = Color.white;
        }

        void DrawPicker()
        {
            var list = Others();
            var lines = new List<string> { "<color=#FFA727>ТЕЛЕПОРТ К КОМУ?</color>   <size=13><color=#BFBFBF>цифра — выбрать, Esc — отмена</color></size>" };
            for (int i = 0; i < list.Count && i < 9; i++) lines.Add($"<color=#FFA727>{i + 1}</color>    {Describe(list[i])}");
            string text = string.Join("\n", lines);
            var size = sPanel.CalcSize(new GUIContent(text));
            GUI.Box(new Rect((Screen.width - size.x) / 2, Screen.height * 0.62f, size.x, size.y), text, sPanel);
        }

        // "Nick  12 м ↑5  желток"
        string Describe(Ghost g)
        {
            string s = g.Nick;
            if (InGame())
            {
                Vector3 me = World.instance.GetEgg().GetPosition();
                float dy = g.Position.y - me.y;
                s += $"  {Vector3.Distance(me, g.Position):0} м" + (Mathf.Abs(dy) > 2 ? (dy > 0 ? $" ↑{dy:0}" : $" ↓{-dy:0}") : "");
            }
            if (g.Yolk) s += "  (разбит)";
            return s;
        }

        void DrawSummary(Rect at)
        {
            var lines = new List<string> { "<color=#FFA727>ИТОГИ КООПЕРАТИВА · ТЕЛЕПОРТЫ</color>", $"Вы ({cfg.Nick.Value}): {tpCounts.Values.Sum()}" };
            foreach (var kv in tpCounts.OrderByDescending(kv => kv.Value)) lines.Add($"    к {kv.Key}: {kv.Value}");
            foreach (var g in Others()) lines.Add($"{g.Nick}: {g.TpTotal}");
            string text = string.Join("\n", lines);
            var size = sPanel.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(at.x, at.y, Mathf.Max(at.width, size.x), size.y), text, sPanel);
        }

        void DrawNameTags()
        {
            var cam = Camera.main;
            if (cam == null || !InGame()) return;
            foreach (var g in ghosts.Values)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.Position + Vector3.up * 1.2f);
                if (sp.z <= 0) continue;
                string label = Describe(g);
                var r = new Rect(sp.x - 160, Screen.height - sp.y - 12, 320, 24);
                GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, tag);
                GUI.color = Color.white; GUI.Label(r, label, tag);
            }
        }

        void DrawHostToggles()
        {
            bool c = EggToggle(cfg.Collide.Value, "Столкновения (иначе проходим сквозь друг друга)");
            bool k = cfg.CrackOnHit.Value;
            GUI.enabled = c;
            k = EggToggle(k, "Удар о друга разбивает скорлупу");
            GUI.enabled = true;
            if (c != cfg.Collide.Value || k != cfg.CrackOnHit.Value)
            {
                cfg.Collide.Value = c; cfg.CrackOnHit.Value = k;
                ApplyOwnSettings();
            }
        }

        bool EggToggle(bool value, string text)
        {
            bool clicked = GUILayout.Button(text, sToggle);
            var r = GUILayoutUtility.GetLastRect();
            var icon = value ? eggIcon : eggOff;
            if (icon != null) GUI.DrawTexture(new Rect(r.x + 4, r.y + (r.height - 18) / 2, 18, 18), icon, ScaleMode.ScaleToFit);
            else GUI.Label(new Rect(r.x + 4, r.y, 24, r.height), value ? "[x]" : "[ ]", sText);
            return clicked ? !value : value;
        }

        void KeyRow(string label, string id, ConfigEntry<KeyCode> key)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, sText, GUILayout.Width(150));
            GUILayout.Label(waitKey == id ? "нажмите клавишу… (Esc — отмена)" : key.Value.ToString(), sText);
            if (waitKey != id && GUILayout.Button("Изменить", sBtn, GUILayout.Width(125))) waitKey = id;
            GUILayout.EndHorizontal();
        }

        void DrawMenu(int _)
        {
            menuScroll = GUILayout.BeginScrollView(menuScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(Mathf.Min(menuContentHeight, Screen.height - 70)));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ник", sText, GUILayout.Width(40));
            cfg.Nick.Value = GUILayout.TextField(cfg.Nick.Value, 24);
            GUILayout.EndHorizontal();

            if (!net.Active)
            {
                GUILayout.Label(("Создать игру").ToUpperInvariant(), sHead);
                DrawHostToggles();
                if (GUILayout.Button("Создать игру (порт " + cfg.Port.Value + ")", sBtn)) { ApplyOwnSettings(); net.Host(); }
                GUILayout.Label(("Подключиться").ToUpperInvariant(), sHead);
                GUILayout.Label("Код приглашения или IP:порт (например, Radmin-адрес хоста)", sDim);
                GUILayout.BeginHorizontal();
                joinText = GUILayout.TextField(joinText, 64);
                if (GUILayout.Button("Подключиться", sBtn, GUILayout.Width(150)) && joinText.Trim().Length > 0)
                {
                    cfg.LastJoin.Value = joinText.Trim();
                    net.Join(joinText);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                string ping = net.Ping.TryGetValue(0, out var hp) ? $" · пинг до хоста {hp} мс" : "";
                GUILayout.Label(net.IsHost ? $"Вы хост · игроков {net.PlayerCount}/{CoopNet.MaxPlayers}" : $"Вы гость · игроков {net.PlayerCount}{ping}", sText);
                if (net.IsHost)
                {
                    GUILayout.Label(("Как друзьям зайти").ToUpperInvariant(), sHead);
                    if (net.Code != null) AddressRow(net.Code, "код приглашения (содержит ваш внешний IP)");
                    if (net.VpnSuspected) GUILayout.Label("У вас, похоже, включён VPN: по коду друзья могут не зайти. Выключите VPN или дайте адрес ниже.", sDim);
                    if (hostAddrs != null) foreach (var a in hostAddrs) { var p = a.Split('|'); AddressRow(p[0], p[1]); }
                    GUILayout.Label(("Настройки хоста").ToUpperInvariant(), sHead);
                    DrawHostToggles();
                }
                else GUILayout.Label((Ghost.Collide ? "Столкновения: вкл" : "Режим призраков: сквозь друг друга") + (Ghost.Collide && Ghost.CrackOnHit ? ", удар разбивает скорлупу" : "") + "  (решает хост)", sDim);

                GUILayout.Label(($"Игроки · ваших телепортов: {tpCounts.Values.Sum()}").ToUpperInvariant(), sHead);
                foreach (var kv in ghosts.OrderBy(kv => kv.Key))
                {
                    var g = kv.Value;
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical();
                    GUILayout.Label(Describe(g), sText);
                    GUILayout.Label((net.Ping.TryGetValue(kv.Key, out var pg) ? $"{pg} мс · " : "") + $"ТП к нему {(tpCounts.TryGetValue(g.Nick, out var n) ? n : 0)} · его ТП {g.TpTotal}", sDim);
                    GUILayout.EndVertical();
                    if (GUILayout.Button("Телепорт", sBtn, GUILayout.Width(125))) TryTeleport(g);
                    GUILayout.EndHorizontal();
                }
                if (ghosts.Count == 0) GUILayout.Label("Пока никого нет", sDim);
                if (world != null) GUILayout.Label($"Общий мир: {world.Count} предметов · ведёте {world.Owned} · у друзей {world.Following}", sDim);
            }

            GUILayout.Label(("Клавиши").ToUpperInvariant(), sHead);
            KeyRow("Меню кооператива", "menu", cfg.MenuKey);
            KeyRow("Телепорт к другу", "tp", cfg.TpKey);
            GUILayout.Label("При двух и более друзьях клавиша телепорта открывает выбор: нажмите цифру нужного игрока.", sDim);

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (net.Active && GUILayout.Button("Отключиться", sBtn)) net.Stop();
            if (GUILayout.Button(showLog ? "Скрыть журнал" : "Журнал", sBtn)) showLog = !showLog;
            if (GUILayout.Button("Сбросить счётчики ТП", sBtn)) { tpCounts.Clear(); cfg.TpStats.Value = ""; }
            GUILayout.EndHorizontal();
            if (showLog) lock (net.Log) foreach (var line in net.Log) GUILayout.Label(line, sDim);
            if (Event.current.type == EventType.Repaint) menuContentHeight = GUILayoutUtility.GetLastRect().yMax + 8;
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        void AddressRow(string address, string note)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(address, sText, GUILayout.Width(175));
            GUILayout.Label(note, sDim);
            if (GUILayout.Button("Копировать", sBtn, GUILayout.Width(125))) { GUIUtility.systemCopyBuffer = address; Toast("Скопировано: " + address); }
            GUILayout.EndHorizontal();
        }

        void OnDestroy() => net?.Stop();
    }
}
