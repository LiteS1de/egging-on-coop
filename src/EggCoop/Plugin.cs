using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace EggCoop
{
    public class CoopConfig
    {
        public ConfigEntry<string> Nick, SignalUrl, StunServers, LastJoin;
        public ConfigEntry<int> Port;
        public ConfigEntry<KeyCode> MenuKey;
    }

    [BepInPlugin("eggcoop", "Egging On Co-op", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        const float SendInterval = 1f / 30f, GhostTimeout = 5f;

        readonly CoopConfig cfg = new CoopConfig();
        readonly Dictionary<int, Ghost> ghosts = new Dictionary<int, Ghost>();
        readonly Dictionary<int, EggState> pending = new Dictionary<int, EggState>();
        CoopNet net;
        bool menuOpen;
        float nextSend;
        Rect window = new Rect(40, 40, 420, 470);
        string joinText = "";
        GUIStyle tag;

        void Awake()
        {
            Log = Logger;
            cfg.Nick = Config.Bind("General", "Nick", "Egg" + Random.Range(100, 999), "Имя над вашим яйцом");
            cfg.MenuKey = Config.Bind("General", "MenuKey", KeyCode.F9, "Клавиша окна кооператива");
            cfg.Port = Config.Bind("Network", "Port", 7788, "UDP-порт хоста");
            cfg.SignalUrl = Config.Bind("Network", "SignalUrl", "https://ntfy.sh", "ntfy-сервер для пробивки NAT");
            cfg.StunServers = Config.Bind("Network", "StunServers", "stun.l.google.com:19302,stun.cloudflare.com:3478", "STUN-серверы через запятую");
            cfg.LastJoin = Config.Bind("Network", "LastJoin", "", "Последний код или IP");
            joinText = cfg.LastJoin.Value;
            net = new CoopNet(this, cfg);
            net.OnState += (id, s) => pending[id] = s;
            net.OnLeave += RemoveGhost;
            ReadTestArgs();
            if (dataDir != null) IsolateData();
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
        bool lab;
        float nextLabStep, lastInput = -100f;
        void LabStep(bool inGame)
        {
            if (!lab) return;
            if (inGame && World.instance.GetEgg().IsInput()) lastInput = Time.unscaledTime;
            if (Time.unscaledTime < nextLabStep) return;
            nextLabStep = Time.unscaledTime + 3f;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != 960)
                Screen.SetResolution(960, 540, FullScreenMode.Windowed);
            var title = inGame ? null : FindObjectOfType<TitleScreen>();
            if (title != null && TitleScreen.menuInitialized) { Log.LogInfo("Lab: Continue"); title.Continue(); nextLabStep += 10f; }
            // The host's egg gets a shove now and then, so the guest has a moving target to follow,
            // but only while nobody plays it: a person at the keyboard wins.
            if (inGame && net.IsHost && Time.unscaledTime - lastInput > 15f && !World.instance.GetEggCore().yolkState)
                World.instance.GetEggCore().eggRigidbody.AddForce(Quaternion.Euler(0, Random.Range(0, 360), 0) * new Vector3(0, 3f, 4f), ForceMode.VelocityChange);
        }

        void RunTestArgs(bool inGame)
        {
            LabStep(inGame);
            if (testMode != null && inGame)
            {
                Log.LogInfo("Test harness: " + testMode);
                if (testMode == "host") net.Host(); else net.Join(testJoin);
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

        void Update()
        {
            Application.runInBackground = true;   // the host must keep sending while alt-tabbed
            if (Input.GetKeyDown(cfg.MenuKey.Value)) SetMenu(!menuOpen);
            net.Poll();

            bool inGame = World.instance != null && World.isPlaying && World.instance.GetEggCore() != null
                && World.instance.GetEggCore().gameObject.activeInHierarchy;
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
                    if (!Ghost.CanBuild) continue;   // need an intact local egg to copy the look from
                    ghosts[kv.Key] = g = new Ghost(kv.Key);
                    Log.LogInfo($"Ghost {kv.Key} ({kv.Value.nick}) appeared");
                }
                g.Push(kv.Value);
                pending.Remove(kv.Key);
            }
            foreach (var kv in ghosts.ToList())
            {
                if (Time.unscaledTime - kv.Value.LastSeen > GhostTimeout) { RemoveGhost(kv.Key); continue; }
                kv.Value.Update(inGame);
            }
        }

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
            };
        }

        void RemoveGhost(int id)
        {
            pending.Remove(id);
            if (ghosts.TryGetValue(id, out var g)) { g.Destroy(); ghosts.Remove(id); }
        }

        // Same moves as the game's own "tp" console command, aimed just above the friend.
        static void TeleportTo(Vector3 pos)
        {
            var egg = World.instance.GetEgg();
            var core = World.instance.GetEggCore();
            pos += Vector3.up * 1.5f;
            if (!core.yolkState) { core.transform.SetPositionAndRotation(pos, Quaternion.identity); egg.SetVelocities(Vector3.zero, Vector3.zero); }
            else egg.GetRawTransform().SetPositionAndRotation(pos, Quaternion.identity);
            World.instance.GetProgress().Teleport(pos.y);
        }

        void SetMenu(bool open)
        {
            menuOpen = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            // Typing a nick must not roll the egg.
            try { Rewired.ReInput.controllers.Keyboard.enabled = !open; } catch { }
        }

        void OnGUI()
        {
            if (tag == null) tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15 };
            DrawNameTags();
            if (menuOpen) window = GUILayout.Window(0x6E99, window, DrawMenu, "Egging On Co-op  (" + cfg.MenuKey.Value + ")");
        }

        void DrawNameTags()
        {
            var cam = Camera.main;
            if (cam == null || World.instance == null || World.instance.GetEgg() == null) return;
            Vector3 me = World.instance.GetEgg().GetPosition();
            foreach (var g in ghosts.Values)
            {
                Vector3 sp = cam.WorldToScreenPoint(g.Position + Vector3.up * 1.2f);
                if (sp.z <= 0) continue;
                float dy = g.Position.y - me.y;
                string label = $"{g.Nick}  {Vector3.Distance(me, g.Position):0} м" + (Mathf.Abs(dy) > 2 ? (dy > 0 ? $"  ↑{dy:0}" : $"  ↓{-dy:0}") : "");
                var r = new Rect(sp.x - 150, Screen.height - sp.y - 12, 300, 24);
                GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), label, tag);
                GUI.color = Color.white; GUI.Label(r, label, tag);
            }
        }

        void DrawMenu(int _)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Ник:", GUILayout.Width(40));
            cfg.Nick.Value = GUILayout.TextField(cfg.Nick.Value, 24);
            GUILayout.EndHorizontal();

            if (!net.Active)
            {
                GUILayout.Space(6);
                if (GUILayout.Button("Создать игру (порт " + cfg.Port.Value + ")", GUILayout.Height(30))) net.Host();
                GUILayout.Space(6);
                GUILayout.Label("Код приглашения или IP:порт:");
                joinText = GUILayout.TextField(joinText, 64);
                if (GUILayout.Button("Подключиться", GUILayout.Height(30)) && joinText.Trim().Length > 0)
                {
                    cfg.LastJoin.Value = joinText.Trim();
                    net.Join(joinText);
                }
            }
            else
            {
                GUILayout.Label(net.IsHost ? $"Вы хост. Игроков: {net.PlayerCount}/{CoopNet.MaxPlayers}" : $"Вы гость. Игроков: {net.PlayerCount}");
                if (net.Code != null)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.TextField(net.Code);
                    if (GUILayout.Button("Копировать", GUILayout.Width(100))) GUIUtility.systemCopyBuffer = net.Code;
                    GUILayout.EndHorizontal();
                    GUILayout.Label("Код содержит ваш внешний IP: отправляйте только друзьям.");
                }
                foreach (var g in ghosts.Values)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(g.Nick);
                    if (World.instance != null && GUILayout.Button("Телепорт к нему", GUILayout.Width(140))) TeleportTo(g.Position);
                    GUILayout.EndHorizontal();
                }
                if (GUILayout.Button("Отключиться")) net.Stop();
            }

            GUILayout.Space(6);
            lock (net.Log) foreach (var line in net.Log) GUILayout.Label(line);
            GUI.DragWindow();
        }

        void OnDestroy() => net?.Stop();
    }
}
