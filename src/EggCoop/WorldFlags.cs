using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

using static EggCoop.Lang;

namespace EggCoop
{
    // Stage 4b/4d: one-way world events (pigeon window, microwave, carnival bell, pipe end, the scene's OneTimeEvents)
    // and the highest drone pad (shared checkpoint). Such progress only moves forward, so every message carries the
    // sender's whole set and receivers apply what they do not have yet: an idempotent OR merge, nothing to order.
    // Not shared on purpose: the owl (it guides each player along their own climb), the frying pan (per egg), the
    // rooster boss (made for one egg) and narrator lines.
    public partial class WorldSync
    {
        const byte WFlags = 7;
        string flagsSent;   // what we last sent; null = send again (new world, new connection)
        float nextFlagCheck;
        readonly Dictionary<int, List<OneTimeEvent>> oneTime = new Dictionary<int, List<OneTimeEvent>>();

        struct Flags { public bool Window, Microwave, Bell, Pipe; public int Drone; public List<int> OneTime; }

        void RegisterFlags()
        {
            oneTime.Clear();
            // OneTimeEvent hashes only: the same list also holds OneTimeFeedbackTrigger hashes (narrator lines), which stay personal.
            foreach (var e in Resources.FindObjectsOfTypeAll<OneTimeEvent>().Where(e => e.gameObject.scene.IsValid()))
            {
                int h = Traverse.Create(e).Field<int>("hash").Value;
                if (!oneTime.TryGetValue(h, out var list)) oneTime[h] = list = new List<OneTimeEvent>();
                list.Add(e);
            }
            Plugin.Log.LogInfo($"World flags: {oneTime.Count} one-time events ({string.Join(", ", oneTime.Values.Select(l => l[0].name).Take(30))})");
        }

        Flags CaptureFlags()
        {
            var s = Persistent.instance.saveData;
            return new Flags
            {
                Window = s.windowBroken, Microwave = s.microwaveOpened, Bell = s.bellBroken, Pipe = s.pipeEndDetached,
                Drone = s.droneMode != CheckpointMode.None ? s.highestDroneLandingHash : 0,
                OneTime = s.activatedOneTimeTriggers.Where(oneTime.ContainsKey).Distinct().OrderBy(h => h).ToList(),
            };
        }

        static string Sig(Flags f) => $"{f.Window}{f.Microwave}{f.Bell}{f.Pipe}/{f.Drone}/{string.Join(",", f.OneTime)}";

        void WatchFlags()
        {
            if (Time.unscaledTime < nextFlagCheck) return;
            nextFlagCheck = Time.unscaledTime + 0.5f;
            var f = CaptureFlags();
            string sig = Sig(f);
            if (sig == flagsSent) return;
            flagsSent = sig;
            SendFlags(f, -1);
        }

        void SendFlags(Flags f, int to)
        {
            var w = net.WorldWriter(); w.Put(WFlags);
            w.Put((byte)((f.Window ? 1 : 0) | (f.Microwave ? 2 : 0) | (f.Bell ? 4 : 0) | (f.Pipe ? 8 : 0)));
            w.Put(f.Drone);
            w.Put((ushort)f.OneTime.Count);
            foreach (var h in f.OneTime) w.Put(h);
            net.SendWorld(w, DeliveryMethod.ReliableOrdered, to);
        }

        static Flags ReadFlags(NetPacketReader r)
        {
            byte m = r.GetByte();
            var f = new Flags { Window = (m & 1) != 0, Microwave = (m & 2) != 0, Bell = (m & 4) != 0, Pipe = (m & 8) != 0, Drone = r.GetInt(), OneTime = new List<int>() };
            for (int n = r.GetUShort(); n > 0; n--) f.OneTime.Add(r.GetInt());
            return f;
        }

        // Each event is replayed the way the game itself shows it, minus what belongs to the player who did it
        // (achievements, narrator comments, the microwave's change of respawn point).
        void ApplyFlags(Flags f, int from)
        {
            var s = Persistent.instance.saveData;   // a copy: read here, write through Persistent.instance.saveData
            var done = new List<string>();
            if (f.Window && !s.windowBroken)
            {
                var w = Any<PigeonWindow>();
                if (w != null && w.isActiveAndEnabled) w.BreakWindow();   // plays the pigeon timeline and sets the flag
                else Persistent.instance.saveData.windowBroken = true;      // OnEnable shows it broken when we get there
                done.Add(T("окно с голубем разбито", "the pigeon window is broken"));
            }
            if (f.Microwave && !s.microwaveOpened)
            {
                Persistent.instance.saveData.microwaveOpened = true;
                if (!s.narratorSpokenDialogues.Contains(3500088)) s.narratorSpokenDialogues.Add(3500088);   // its "you opened it" line
                var mw = Any<Microwave>();
                if (mw != null && mw.isActiveAndEnabled)
                {
                    Call(mw, "openFeedback", "PlayFeedbacks");
                    Call(mw, "timeline", "Play");
                }
                done.Add(T("микроволновка открыта", "the microwave is open"));
            }
            if (f.Bell && !s.bellBroken)
            {
                Persistent.instance.saveData.bellBroken = true;
                var b = Any<CarnivalBellMechanism>();
                if (b != null)
                {
                    var t = Traverse.Create(b);
                    t.Field<GameObject>("bellParent").Value?.SetActive(false);   // its Start ran already: hide it ourselves
                    var broken = t.Field<RayFire.RayfireRigid>("brokenBellParent").Value;
                    if (b.isActiveAndEnabled && broken != null)
                    {
                        Call(b, "brokenBellFeedback", "PlayFeedbacks");
                        broken.gameObject.SetActive(true);
                        broken.Demolish();
                        t.Field<RayFire.RayfireBomb>("bellBomb").Value?.Explode(Time.deltaTime);
                    }
                }
                done.Add(T("колокол разбит", "the bell is broken"));
            }
            if (f.Pipe && !s.pipeEndDetached)
            {
                Persistent.instance.saveData.pipeEndDetached = true;
                var p = Any<PipeEnd>();
                if (p != null)
                {
                    var t = Traverse.Create(p);
                    t.Field<bool>("detached").Value = true;
                    var rb = t.Field<Rigidbody>("rb").Value;
                    if (rb != null) rb.useGravity = true;
                    if (p.isActiveAndEnabled) Call(p, "detachFeedback", "PlayFeedbacks");
                }
                done.Add(T("труба отвалилась", "the pipe fell off"));
            }
            int events = 0;
            foreach (var h in f.OneTime)
            {
                if (s.activatedOneTimeTriggers.Contains(h) || !oneTime.TryGetValue(h, out var list)) continue;
                var live = list.FirstOrDefault(e => e != null && e.isActiveAndEnabled);
                if (live != null) live.Activate();   // plays its feedback and records the hash
                else s.activatedOneTimeTriggers.Add(h);   // OnEnable switches its objects when we get there
                Plugin.Log.LogInfo($"World flags: one-time event {list[0].name} from player {from} ({(live != null ? "played" : "recorded")})");
                events++;
            }
            if (ApplyDrone(f.Drone)) done.Add(T("новая площадка дрона — можно лететь к ней", "a new drone pad: you can fly to it"));
            if (done.Count > 0) Plugin.Notify($"{Plugin.NickOf(from)}: {string.Join(", ", done)}");
            if (done.Count > 0 || events > 0) Plugin.Log.LogInfo($"World flags from player {from}: {string.Join(", ", done)}; {events} one-time events");
        }

        // Shared checkpoint: a friend reached a higher drone pad. Only with the game's checkpoints on (drone or phoenix).
        static readonly System.Reflection.FieldInfo splinesField = AccessTools.Field(typeof(DronePaths), "splines");
        static bool ApplyDrone(int hash)
        {
            var dp = DronePaths.instance;
            var s = Persistent.instance.saveData;
            if (dp == null || hash == 0 || s.droneMode == CheckpointMode.None || hash == s.highestDroneLandingHash) return false;
            if (!(splinesField?.GetValue(dp) is System.Collections.IDictionary splines) || !splines.Contains(hash)) return false;
            if (!dp.TryActivateLanding(hash)) return false;   // ours is higher already
            foreach (var a in Resources.FindObjectsOfTypeAll<DroneLandingActivator>().Where(a => a.gameObject.scene.IsValid()))
            {
                var t = Traverse.Create(a);
                int h = t.Field<bool>("unlockOverride").Value ? t.Field<int>("overrideHash").Value : t.Field<int>("landingHash").Value;
                if (h == hash) t.Field<DroneCallButton>("button").Value?.Activate();
            }
            dp.ActivateLowerLandings();
            return true;
        }

        // Feel's MMF_Player and PlayableDirector live in assemblies we do not reference: call them by name.
        static void Call(Component owner, string field, string method)
        {
            var target = Traverse.Create(owner).Field(field).GetValue();
            if (target != null) Traverse.Create(target).Method(method, new System.Type[0]).GetValue();   // the no-argument overload
        }

#if LAB
        // -coop-flagtest: pretend player 1 did everything (all flags, every one-time event, the highest drone pad).
        public void LabFlags()
        {
            int top = 0; float topY = float.MinValue;
            if (splinesField?.GetValue(DronePaths.instance) is System.Collections.IDictionary sp)
                foreach (System.Collections.DictionaryEntry kv in sp)
                {
                    var y = Traverse.Create(kv.Value).Field<Vector3>("position").Value.y;
                    if (y > topY) { topY = y; top = (int)kv.Key; }
                }
            Plugin.Log.LogInfo($"Lab flags: before {LabFlagState()} droneMode={Persistent.instance.saveData.droneMode} pads={(splinesField?.GetValue(DronePaths.instance) as System.Collections.IDictionary)?.Count}");
            ApplyFlags(new Flags { Window = true, Microwave = true, Bell = true, Pipe = true, Drone = top, OneTime = oneTime.Keys.ToList() }, 1);
        }

        public string LabFlagState()
        {
            GameObject F(Component c, string field) => c != null ? Traverse.Create(c).Field<GameObject>(field).Value : null;
            string A(GameObject g) => g == null ? "-" : g.activeSelf ? "on" : "off";
            var w = Any<PigeonWindow>(); var m = Any<Microwave>(); var b = Any<CarnivalBellMechanism>();
            return $"{Sig(CaptureFlags())} windowIntact={A(F(w, "pigeonWindowIntact"))} windowBroken={A(F(w, "pigeonWindowBroken"))} bell={A(F(b, "bellParent"))} " +
                $"door={A(F(m, "door"))} openedDoor={A(F(m, "openedDoor"))} sparrow={A(F(m, "sparrow"))} freeSparrow={A(F(m, "freeSparrow"))} highestPad={Persistent.instance.saveData.highestDroneLandingHash}";
        }

        // Where to look at each event for screenshots.
        public List<(string name, Vector3 at)> LabSights() => new List<(string, Vector3)>
        {
            ("window", Any<PigeonWindow>()?.transform.position ?? Vector3.zero), ("microwave", Any<Microwave>()?.transform.position ?? Vector3.zero),
            ("bell", Any<CarnivalBellMechanism>()?.transform.position ?? Vector3.zero), ("pipe", Any<PipeEnd>()?.transform.position ?? Vector3.zero),
        };
#endif

        static T Any<T>() where T : Component => Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x => x.gameObject.scene.IsValid());
    }
}
