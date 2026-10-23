using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

using static EggCoop.Lang;

namespace EggCoop
{
    // Host's game clock as seen by this client. Moving platforms (KinematicCurveMovement) run on Time.time, which
    // starts at each player's game launch, so the same platform used to be in a different place for everyone.
    public static class SharedClock
    {
        public static float Offset;   // host Time.time - our Time.time
        public static float Time => UnityEngine.Time.time + Offset;
        static bool synced;

        public static void Sample(float hostTime)
        {
            float o = hostTime - UnityEngine.Time.time;
            // First sample (or a big jump, e.g. the host restarted) snaps; later ones only nudge, so platforms never stutter.
            Offset = !synced || Mathf.Abs(o - Offset) > 0.5f ? o : Mathf.Lerp(Offset, o, 0.1f);
            synced = true;
        }

        public static void Reset() { Offset = 0f; synced = false; }

        // KinematicCurveMovement.FixedUpdate: every Time.time becomes SharedClock.Time.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var real = AccessTools.PropertyGetter(typeof(UnityEngine.Time), nameof(UnityEngine.Time.time));
            var shared = AccessTools.PropertyGetter(typeof(SharedClock), nameof(Time));
            foreach (var c in code) yield return c.Calls(real) ? new CodeInstruction(System.Reflection.Emit.OpCodes.Call, shared) : c;
        }
    }

    // One shared world: every loose physics object (pots, crates, bottles, buttons, boards, pool balls) is simulated by
    // exactly one player at a time, its "owner", who streams its pose; everyone else holds it kinematic and follows.
    // The egg that touches a free object claims it, the host settles races (first claim wins), objects knocked by an
    // owned object join the owner, and an object that comes to rest is released.
    // Objects are matched across PCs by scene + hierarchy path (same game version = same path).
    public partial class WorldSync
    {
        const byte WState = 1, WClaim = 2, WOwner = 3, WSnapReq = 4, WSnap = 5, WBroken = 6;
        const float SendInterval = 0.05f, RestTime = 1f, TouchHold = 0.5f;

        internal class Body
        {
            public int Id;
            public Rigidbody Rb;
            public Group G;
            public RigidDataHolder Holder;
            public readonly List<(float t, Vector3 p, Quaternion r)> Buf = new List<(float, Vector3, Quaternion)>();
        }

        internal class Group
        {
            public int Id;
            public readonly List<Body> Bodies = new List<Body>();
            public int Owner = -1;
            public float TouchedAt = -100f, MovingAt = -100f;   // last time our egg touched it / it moved (owner side)
            public float OwnerTouchAt = -100f;                    // host: last time its owner reported touching it
        }

        readonly CoopNet net;
        readonly Dictionary<int, Body> bodies = new Dictionary<int, Body>();
        readonly Dictionary<Rigidbody, Body> byRb = new Dictionary<Rigidbody, Body>();
        readonly Dictionary<int, Group> groups = new Dictionary<int, Group>();
        readonly HashSet<int> knownBroken = new HashSet<int>();
        bool registered, snapshotAsked, wasConnected;
        World registeredFor;   // the game rebuilds World on "Save & Quit", quick restart and reloads: register again then
        float nextSend;
        static readonly FieldInfoCache StoreState = new FieldInfoCache();

        public int Count => bodies.Count;
        public int LastRemoteBody;   // lab: the object a friend moved last
        public int Owned => groups.Values.Count(g => g.Owner == net.MyId);
        public int Following => groups.Values.Count(g => g.Owner >= 0 && g.Owner != net.MyId);

        public WorldSync(CoopNet net, Harmony harmony)
        {
            this.net = net;
            net.OnWorld += Receive;
            harmony.Patch(AccessTools.Method(typeof(KinematicCurveMovement), "FixedUpdate"),
                transpiler: new HarmonyMethod(typeof(SharedClock), nameof(SharedClock.Transpiler)));
        }

        // ------------------------------------------------------------ registration

        public void Register()
        {
            if (registered && registeredFor == World.instance) return;
            if (registered) Unregister();
            registered = true;
            registeredFor = World.instance;
            var egg = World.instance.GetEggCore().eggRigidbody;
            var determiner = World.instance.GetEgg().determiner;
            int dup = 0;
            foreach (var rb in Resources.FindObjectsOfTypeAll<Rigidbody>())
            {
                var go = rb.gameObject;
                if (!go.scene.IsValid() || go.scene.name == "DontDestroyOnLoad" || rb == egg || rb == determiner) continue;
                var holder = rb.GetComponent<RigidDataHolder>();
                if (rb.isKinematic && holder == null) continue;   // platforms and animated parts: the shared clock handles those
                if (rb.GetComponentInParent<EggCoreData>(true) != null || rb.GetComponentInParent<RawEggController>(true) != null
                    || rb.GetComponent<RayFire.RayfireRigid>() != null || go.name.Contains("(Clone)") || go.name == "FreeCamRB") continue;
                int id = Hash(go.scene.name + "/" + PathOf(rb.transform));
                if (bodies.ContainsKey(id)) { dup++; continue; }
                var b = new Body { Id = id, Rb = rb, Holder = holder };
                bodies[id] = b; byRb[rb] = b;
                go.AddComponent<WorldTouch>().Init(this, b);
            }
            // Bodies joined by joints (a pot and its handle) move together: one owner per group.
            foreach (var b in bodies.Values) if (b.G == null) Join(b, new Group { Id = b.Id });
            foreach (var j in Resources.FindObjectsOfTypeAll<Joint>())
                if (j.connectedBody != null && j.GetComponent<Rigidbody>() is Rigidbody jr && byRb.TryGetValue(jr, out var a) && byRb.TryGetValue(j.connectedBody, out var c) && a.G != c.G)
                    Merge(a.G, c.G);
            foreach (var g in bodies.Values.Select(b => b.G).Distinct()) groups[g.Id] = g;
            foreach (var id in Persistent.instance.saveData.brokenObjects) knownBroken.Add(id);
            RegisterFlags();
            Plugin.Log.LogInfo($"World sync: {bodies.Count} bodies in {groups.Count} groups ({dup} duplicate paths skipped)");
        }

        void Unregister()
        {
            bodies.Clear(); byRb.Clear(); groups.Clear(); knownBroken.Clear();
            registered = false; snapshotAsked = false; flagsSent = null;
            Plugin.Log.LogInfo("World sync: world was rebuilt, registering again");
        }

        // A player left: whatever they held is released (the host sends its own view of the poses to everyone).
        public void PlayerLeft(int id)
        {
            if (!registered) return;
            foreach (var g in groups.Values.Where(g => g.Owner == id).ToList())
                if (net.IsHost) Release(g); else SetOwner(g, -1);
        }

        void Join(Body b, Group g) { b.G = g; g.Bodies.Add(b); }

        void Merge(Group into, Group from)
        {
            foreach (var b in from.Bodies) Join(b, into);
            from.Bodies.Clear();
            into.Id = Mathf.Min(into.Id, from.Id);
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }

        // FNV-1a: string.GetHashCode differs between runtimes, this does not.
        static int Hash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }

        // ------------------------------------------------------------ frame

        public void Update()
        {
            bool conn = net.Connected;
            if (conn != wasConnected && registered)
            {
                // Lost the link: let go of what friends were moving. Back again: ask the host for its world once more.
                if (!conn) foreach (var g in groups.Values) if (g.Owner != net.MyId) SetOwner(g, -1);
                snapshotAsked = false; flagsSent = null;
            }
            wasConnected = conn;
            if (!registered || !conn || registeredFor != World.instance) return;
            if (!net.IsHost && !snapshotAsked) { snapshotAsked = true; var w = net.WorldWriter(); w.Put(WSnapReq); net.SendWorld(w, DeliveryMethod.ReliableOrdered); }
            float now = Time.unscaledTime;
            WatchBroken();
            WatchFlags();
            if (now < nextSend) return;
            nextSend = now + SendInterval;

            var w2 = net.WorldWriter();
            w2.Put(WState);
            int countPos = w2.Length; w2.Put((ushort)0);
            int n = 0;
            foreach (var g in groups.Values)
            {
                if (g.Owner != net.MyId) continue;
                bool moving = g.Bodies.Any(b => b.Rb != null && !b.Rb.IsSleeping() && b.Rb.velocity.sqrMagnitude > 0.0025f);
                if (moving) g.MovingAt = now;
                bool touching = now - g.TouchedAt < TouchHold;
                if (!moving && !touching && now - g.MovingAt > RestTime && now - g.TouchedAt > RestTime) { Release(g); continue; }
                foreach (var b in g.Bodies)
                {
                    if (b.Rb == null) continue;
                    WritePose(w2, b); w2.Put(touching); n++;
                }
            }
            if (n == 0) return;
            System.Array.Copy(System.BitConverter.GetBytes((ushort)n), 0, w2.Data, countPos, 2);
            net.SendWorld(w2, DeliveryMethod.Sequenced);
        }

        // Followers: render each foreign-owned body slightly in the past, like the egg ghosts.
        public void FixedUpdate()
        {
            float t = Time.unscaledTime - Ghost.Delay;
            foreach (var g in groups.Values)
            {
                if (g.Owner < 0 || g.Owner == net.MyId) continue;
                foreach (var b in g.Bodies)
                {
                    if (b.Rb == null || b.Buf.Count == 0) continue;
                    int i = b.Buf.FindLastIndex(x => x.t <= t);
                    var a = b.Buf[Mathf.Max(i, 0)]; var c = a; float k = 0;
                    if (i >= 0 && i + 1 < b.Buf.Count) { c = b.Buf[i + 1]; k = Mathf.InverseLerp(a.t, c.t, t); }
                    b.Rb.MovePosition(Vector3.Lerp(a.p, c.p, k));
                    b.Rb.MoveRotation(Quaternion.Slerp(a.r, c.r, k));
                    if (i > 0) b.Buf.RemoveRange(0, i);
                }
            }
        }

        // ------------------------------------------------------------ ownership

        // Called by WorldTouch from the physics callbacks of every synced body.
        internal void Touched(Body b, Rigidbody other)
        {
            if (!registered || !net.Connected || other == null) return;
            var core = World.instance.GetEggCore();
            bool byEgg = other == core.eggRigidbody || other.CompareTag("RawEgg");
            bool byOwned = byRb.TryGetValue(other, out var ob) && ob.G.Owner == net.MyId && ob.G != b.G;
            if (!byEgg && !byOwned) return;
            var g = b.G;
            if (byEgg) g.TouchedAt = Time.unscaledTime;
            if (g.Owner == net.MyId) return;
            if (g.Owner >= 0 && !net.IsHost) return;   // someone else holds it; they keep it while they touch it
            Claim(g);
        }

        void Claim(Group g)
        {
            if (net.IsHost) { Decide(g, net.MyId); return; }
            SetOwner(g, net.MyId);   // optimistic: we are touching it right now; the host may still say no
            var w = net.WorldWriter(); w.Put(WClaim); w.Put(g.Id);
            net.SendWorld(w, DeliveryMethod.ReliableOrdered);
        }

        // Host: first claim wins; a claim on an object its owner no longer touches moves it over.
        void Decide(Group g, int claimer)
        {
            float now = Time.unscaledTime;
            bool ownerHolds = g.Owner >= 0 && g.Owner != claimer
                && (g.Owner == net.MyId ? now - g.TouchedAt < TouchHold : now - g.OwnerTouchAt < TouchHold);
            int owner = ownerHolds ? g.Owner : claimer;
            if (owner == claimer) g.OwnerTouchAt = now;
            SetOwner(g, owner);
            var w = net.WorldWriter(); w.Put(WOwner); w.Put(g.Id); w.Put((sbyte)owner);
            net.SendWorld(w, DeliveryMethod.ReliableOrdered);
        }

        void Release(Group g)
        {
            // Final resting poses, reliably, so every copy ends where the owner's did.
            var w = net.WorldWriter(); w.Put(WOwner); w.Put(g.Id); w.Put((sbyte)-1);
            w.Put((ushort)g.Bodies.Count);
            foreach (var b in g.Bodies) WritePose(w, b);
            net.SendWorld(w, DeliveryMethod.ReliableOrdered);
            SetOwner(g, -1);
        }

        void SetOwner(Group g, int owner)
        {
            if (g.Owner == owner) return;
            bool follow = owner >= 0 && owner != net.MyId;
            g.Owner = owner;
            foreach (var b in g.Bodies)
            {
                if (b.Rb == null) continue;
                b.Buf.Clear();
                b.Rb.isKinematic = follow;
                if (follow && b.Holder != null) StoreState.MarkMoved(b.Holder);   // or the game "restores" it while we move it
            }
        }

        // ------------------------------------------------------------ receive

        void Receive(int from, NetPacketReader r)
        {
            if (!registered || registeredFor != World.instance) return;   // not in the world yet: a snapshot is requested once we are
            try { ReceiveUnsafe(from, r); }
            catch (System.Exception e) { Plugin.Warn("world message", e); }
        }

        void ReceiveUnsafe(int from, NetPacketReader r)
        {
            byte type = r.GetByte();
            float now = Time.unscaledTime;
            switch (type)
            {
                case WState:
                    for (int n = r.GetUShort(); n > 0; n--)
                    {
                        ReadPose(r, out var id, out var p, out var q);
                        bool touching = r.GetBool();
                        if (!bodies.TryGetValue(id, out var b)) continue;
                        LastRemoteBody = id;
                        if (net.IsHost && b.G.Owner == from && touching) b.G.OwnerTouchAt = now;
                        if (b.G.Owner != from) { if (net.IsHost) continue; SetOwner(b.G, from); }   // late owner message
                        b.Buf.Add((now, p, q));
                        if (b.Buf.Count > 30) b.Buf.RemoveAt(0);
                    }
                    break;
                case WClaim:
                    if (net.IsHost && groups.TryGetValue(r.GetInt(), out var cg)) Decide(cg, from);
                    break;
                case WOwner:
                {
                    int gid = r.GetInt(); int owner = r.GetSByte();
                    if (!groups.TryGetValue(gid, out var g)) break;
                    if (owner < 0 && r.AvailableBytes >= 2)
                        for (int n = r.GetUShort(); n > 0; n--) { ReadPose(r, out var id, out var p, out var q); Place(id, p, q); }
                    if (net.IsHost && owner < 0 && g.Owner != from) break;   // stale release
                    SetOwner(g, owner);
                    break;
                }
                case WSnapReq:
                    if (net.IsHost) { SendSnapshot(from); SendFlags(CaptureFlags(), from); }
                    break;
                case WSnap:
                {
                    int total = r.GetUShort(), missing = 0;
                    for (int n = total; n > 0; n--)
                    {
                        ReadPose(r, out var id, out var p, out var q); int owner = r.GetSByte();
                        if (!bodies.TryGetValue(id, out var b)) { missing++; continue; }
                        Place(id, p, q);
                        if (owner >= 0 && owner != net.MyId) SetOwner(b.G, owner);
                    }
                    for (int n = r.GetUShort(); n > 0; n--) Break(r.GetInt());
                    Plugin.Log.LogInfo($"World sync: host world applied, {missing} of {total} host objects not found here");
                    // Objects are matched by their place in the scene: many misses mean a different build of the game.
                    if (missing > total / 10) Plugin.Notify(T($"{missing} из {total} предметов хоста не нашлись у вас: похоже, у вас разные версии игры", $"{missing} of {total} host objects are missing here: you probably have different game versions"));
                    break;
                }
                case WBroken:
                    Break(r.GetInt());
                    break;
                case WFlags:
                    ApplyFlags(ReadFlags(r), from);
                    break;
            }
        }

        void SendSnapshot(int to)
        {
            var w = net.WorldWriter(); w.Put(WSnap);
            w.Put((ushort)bodies.Count);
            foreach (var b in bodies.Values) { WritePose(w, b); w.Put((sbyte)b.G.Owner); }
            var broken = Persistent.instance.saveData.brokenObjects;
            w.Put((ushort)broken.Count);
            foreach (var id in broken) w.Put(id);
            net.SendWorld(w, DeliveryMethod.ReliableOrdered, to);
            Plugin.Log.LogInfo($"World sync: snapshot ({bodies.Count} bodies, {broken.Count} broken) sent to player {to}");
        }

        void Place(int id, Vector3 p, Quaternion q)
        {
            if (!bodies.TryGetValue(id, out var b) || b.Rb == null) return;
            b.Rb.position = p; b.Rb.rotation = q;
            b.Rb.transform.SetPositionAndRotation(p, q);
            if (!b.Rb.isKinematic) { b.Rb.velocity = Vector3.zero; b.Rb.angularVelocity = Vector3.zero; b.Rb.Sleep(); }
            if (b.Holder != null) StoreState.MarkMoved(b.Holder);
        }

        // ------------------------------------------------------------ breakables

        // The game lists broken bottles/planks in saveData.brokenObjects by their game ID; mirror new entries.
        void WatchBroken()
        {
            foreach (var id in Persistent.instance.saveData.brokenObjects)
            {
                if (!knownBroken.Add(id)) continue;
                var w = net.WorldWriter(); w.Put(WBroken); w.Put(id);
                net.SendWorld(w, DeliveryMethod.ReliableOrdered);
            }
        }

        void Break(int gameId)
        {
            knownBroken.Add(gameId);
            var list = Persistent.instance.saveData.brokenObjects;
            if (!list.Contains(gameId)) list.Add(gameId);
            // shortcut: the object just disappears (no shatter effect); add the RayFire demolition if it looks too abrupt.
            int hidden = 0;
            foreach (var bo in Resources.FindObjectsOfTypeAll<BreakableObject>())
                if (bo.ID == gameId && bo.gameObject.scene.IsValid() && bo.gameObject.activeSelf) { bo.gameObject.SetActive(false); hidden++; }
            Plugin.Log.LogInfo($"World sync: object {gameId} broken by a friend ({hidden} hidden here)");
        }

        // ------------------------------------------------------------ wire

        static void WritePose(NetDataWriter w, Body b)
        {
            var p = b.Rb.position; var q = b.Rb.rotation;
            w.Put(b.Id); w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
        }

        static void ReadPose(NetPacketReader r, out int id, out Vector3 p, out Quaternion q)
        {
            id = r.GetInt();
            p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            q = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        }

#if LAB
        // Lab: claim the free body nearest to our egg and throw it; returns its id.
        public int TestPush(Vector3 near)
        {
            var b = bodies.Values.Where(x => x.Rb != null && x.Rb.gameObject.activeInHierarchy && x.G.Owner < 0 && x.G.Bodies.Count == 1)
                .OrderBy(x => (x.Rb.position - near).sqrMagnitude).FirstOrDefault();
            if (b == null) return 0;
            b.G.TouchedAt = Time.unscaledTime;
            Claim(b.G);
            b.Rb.WakeUp();
            b.Rb.velocity = new Vector3(Random.Range(-2f, 2f), 4f, Random.Range(-2f, 2f));
            return b.Id;
        }
        // Lab: the free single body nearest to a point (to drop our egg on it and see the touch claim it).
        public int Nearest(Vector3 near, out Vector3 pos)
        {
            var b = bodies.Values.Where(x => x.Rb != null && x.Rb.gameObject.activeInHierarchy && x.G.Owner < 0 && x.G.Bodies.Count == 1)
                .OrderBy(x => (x.Rb.position - near).sqrMagnitude).FirstOrDefault();
            pos = b != null ? b.Rb.position : Vector3.zero;
            return b != null ? b.Id : 0;
        }
        // Lab: first registered body whose name contains `part` (any group size).
        public int Find(string part, out Vector3 pos)
        {
            var b = bodies.Values.Where(x => x.Rb != null && x.Rb.name.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Rb.name).FirstOrDefault();
            pos = b != null ? b.Rb.position : Vector3.zero;
            return b != null ? b.Id : 0;
        }
        // Lab: claim this body's group (as if our egg touched it) and throw the body.
        public void Push(int id, Vector3 velocity)
        {
            if (!bodies.TryGetValue(id, out var b) || b.Rb == null) return;
            b.G.TouchedAt = Time.unscaledTime;
            Claim(b.G);
            if (b.Rb.isKinematic) return;   // someone else holds it: the host said no
            b.Rb.WakeUp();
            b.Rb.velocity = velocity;
        }
        public string Describe(int id) => bodies.TryGetValue(id, out var b) && b.Rb != null
            ? System.FormattableString.Invariant($"{b.Rb.name} owner={b.G.Owner} pos=({b.Rb.position.x:F2},{b.Rb.position.y:F2},{b.Rb.position.z:F2})") : "unknown";
#endif

        public void Reset()
        {
            snapshotAsked = false; flagsSent = null;
            foreach (var g in groups.Values) SetOwner(g, -1);
            SharedClock.Reset();
        }

        // RigidDataHolder.storeData.state: 0 = never moved (the game snaps it back home if it strays), 1 = moved.
        class FieldInfoCache
        {
            readonly System.Reflection.FieldInfo store = AccessTools.Field(typeof(RigidDataHolder), "storeData");
            public void MarkMoved(RigidDataHolder h)
            {
                var d = (RigidData)store.GetValue(h);
                if (d.state == 0) { d.state = 1; store.SetValue(h, d); }
            }
        }
    }

    // Forwards the physics callbacks of a synced body to WorldSync.
    public class WorldTouch : MonoBehaviour
    {
        WorldSync sync;
        WorldSync.Body body;
        internal void Init(WorldSync s, WorldSync.Body b) { sync = s; body = b; }
        void OnCollisionEnter(Collision c) => sync.Touched(body, c.rigidbody);
        void OnCollisionStay(Collision c) => sync.Touched(body, c.rigidbody);
    }
}
