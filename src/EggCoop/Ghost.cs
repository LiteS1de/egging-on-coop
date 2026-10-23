using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace EggCoop
{
    // A friend's egg: a visual copy of the local egg's shell (no game scripts), driven by snapshots ~100 ms in the past.
    // With collisions on, it also carries a copy of the egg collider on a kinematic body, so the local egg can bump
    // into it and stand on it. The body is moved in FixedUpdate, which gives the physics engine real contact velocities.
    public class Ghost
    {
        const float SnapDistance = 8f;
        const float MaxPenetration = 0.01f;
        public static float Delay = 0.1f;   // render this far in the past so there is always a next snapshot
        public static bool Collide = true, CrackOnHit;   // the host's choice, see CoopNet.OnSettings

        public string Nick = "";
        public float LastSeen;
        public ushort TpTotal;
        public Vector3 Position => root.transform.position;
        public bool Yolk => yolkNow;
        public bool Fresh => Time.unscaledTime - LastSeen < 1f;

        readonly GameObject root, shell, yolk;
        readonly Rigidbody body;
        readonly Collider col;
        readonly List<Renderer> shellRenderers = new List<Renderer>();
        readonly List<(float t, EggState s)> buffer = new List<(float, EggState)>();
        int skin = int.MinValue;
        bool yolkNow, snapped = true, hasSeq;
        byte tpSeq;
        float collideAfter;
        Material localSkinMaterial;

        // Our shell must exist and not be the one flying apart after a full crack (that one is replaced shortly).
        public static bool CanBuild => World.instance != null && World.instance.GetEgg() != null && World.instance.GetEggCore() != null
            && World.instance.GetEgg().GetShell() != null && !World.instance.GetEgg().GetShell().cracking;

        public Ghost(int id)
        {
            root = new GameObject("EggCoop_Ghost_" + id);
            Object.DontDestroyOnLoad(root);
            shell = new GameObject("shell");
            shell.transform.SetParent(root.transform, false);

            // Copy every visible mesh of the local shell, positioned relative to the egg body. Shards use their
            // resting pose: right after a rebuild they are scaled to 0 and grow back, and a copy taken then was invisible.
            var core = World.instance.GetEggCore();
            var ct = core.transform;
            var eggShell = World.instance.GetEgg().GetShell();
            var restPos = HarmonyLib.AccessTools.Field(typeof(EggShell), "originalLocalPositions")?.GetValue(eggShell) as Dictionary<Collider, Vector3>;
            float restScale = eggShell.GetOriginalScale();
            var coreFrame = Matrix4x4.TRS(ct.position, ct.rotation, Vector3.one).inverse;
            foreach (var mr in eggShell.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (!mr.enabled || mf == null || mf.sharedMesh == null) continue;
                var shard = mr.GetComponent<Collider>();
                Matrix4x4 world;
                if (shard != null && restPos != null && restPos.TryGetValue(shard, out var lp))
                    world = mr.transform.parent.localToWorldMatrix * Matrix4x4.TRS(lp, eggShell.GetOriginalRotation(shard), Vector3.one * restScale);
                else if (mr.gameObject.activeInHierarchy) world = mr.transform.localToWorldMatrix;
                else continue;
                var m = coreFrame * world;
                var go = new GameObject(mr.name);
                go.transform.SetParent(shell.transform, false);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
                shellRenderers.Add(r);
            }
            localSkinMaterial = SkinMaterial(Persistent.instance.saveData.currentSkinID);

            yolk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(yolk.GetComponent<Collider>());
            yolk.transform.SetParent(root.transform, false);
            yolk.transform.localScale = new Vector3(0.9f, 0.35f, 0.9f);
            if (shellRenderers.Count > 0)
            {
                var m = new Material(shellRenderers[0].sharedMaterial) { mainTexture = null, color = new Color(1f, 0.75f, 0.1f) };
                m.SetColor("_BaseColor", new Color(1f, 0.75f, 0.1f));
                yolk.GetComponent<Renderer>().sharedMaterial = m;
            }
            yolk.SetActive(false);

            // Physical body: "Dynamic" layer, so the egg treats the friend like a crate it can push against and stand on.
            body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var colGo = new GameObject("collider");
            colGo.transform.SetParent(root.transform, false);
            colGo.transform.localScale = ct.lossyScale;
            colGo.layer = LayerUtility.dynamicLayer;
            col = CopyCollider(core.eggCollider, colGo);
            col.excludeLayers = ~LayerUtility.eggMask;   // touches only our egg: pots and planks in our world ignore the friend
            col.hasModifiableContacts = true;
            col.enabled = false;
            Soften(col.GetInstanceID(), true, core.eggRigidbody.mass);
            root.AddComponent<GhostMover>().ghost = this;
            var size = Vector3.zero;
            foreach (var r in shellRenderers) size = Vector3.Max(size, r.bounds.size);
            Plugin.Log.LogInfo(System.FormattableString.Invariant($"Ghost {id} built: {shellRenderers.Count} meshes, largest {size.magnitude:0.00} m"));
        }

        static Collider CopyCollider(Collider src, GameObject go)
        {
            Collider c;
            switch (src)
            {
                case CapsuleCollider s: var cc = go.AddComponent<CapsuleCollider>(); cc.center = s.center; cc.radius = s.radius; cc.height = s.height; cc.direction = s.direction; c = cc; break;
                case SphereCollider s: var sc = go.AddComponent<SphereCollider>(); sc.center = s.center; sc.radius = s.radius; c = sc; break;
                case BoxCollider s: var bc = go.AddComponent<BoxCollider>(); bc.center = s.center; bc.size = s.size; c = bc; break;
                case MeshCollider s: var mc = go.AddComponent<MeshCollider>(); mc.convex = true; mc.sharedMesh = s.sharedMesh; c = mc; break;
                default: var fb = go.AddComponent<SphereCollider>(); fb.radius = 0.5f; c = fb; break;
            }
            c.sharedMaterial = src.sharedMaterial;
            return c;
        }

        // A kinematic ghost cannot be pushed back, so any overlap with our egg used to be resolved by launching our
        // egg at the engine's depenetration speed (~10 m/s). Contacts with ghosts get a small penetration and a
        // capped impulse: a friend can still push you and carry you, but never fling you.
        static volatile HashSet<int> ghostColliders = new HashSet<int>();
        static float maxImpulse = 0.5f;
        static bool hooked;
        static void Soften(int colliderId, bool add, float eggMass = 1f)
        {
            var next = new HashSet<int>(ghostColliders);
            if (add) next.Add(colliderId); else next.Remove(colliderId);
            ghostColliders = next;
            maxImpulse = eggMass * 0.5f;   // per contact per physics step: up to 0.5 m/s of push
            if (!hooked) { Physics.ContactModifyEvent += OnContactModify; hooked = true; }
        }

        // Runs on a physics worker thread: no Unity API here.
        static void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            var set = ghostColliders;
            for (int p = 0; p < pairs.Length; p++)
            {
                var pair = pairs[p];
                if (!set.Contains(pair.colliderInstanceID) && !set.Contains(pair.otherColliderInstanceID)) continue;
                for (int i = 0; i < pair.contactCount; i++)
                {
                    pair.SetMaxImpulse(i, maxImpulse);
                    if (pair.GetSeparation(i) < -MaxPenetration) pair.SetSeparation(i, -MaxPenetration);
                }
            }
        }

        public void Push(EggState s)
        {
            float now = Time.unscaledTime;
            LastSeen = now;
            Nick = s.nick;
            TpTotal = s.tpTotal;
            bool teleported = hasSeq && s.tpSeq != tpSeq;
            tpSeq = s.tpSeq; hasSeq = true;
            if (teleported || (buffer.Count > 0 && (buffer[buffer.Count - 1].s.pos - s.pos).sqrMagnitude > SnapDistance * SnapDistance))
            {
                buffer.Clear();
                snapped = true;   // jump there without sweeping through anything on the way
                PauseCollisions();
            }
            buffer.Add((now, s));
            if (buffer.Count > 30) buffer.RemoveAt(0);
            if (s.skin != skin) ApplySkin(s.skin);
        }

        // After a teleport (theirs or ours) let the teleporter land first; then the softened contacts slide two
        // overlapping eggs apart gently (waiting for them to separate on their own left eggs inside each other).
        public void PauseCollisions()
        {
            col.enabled = false;
            collideAfter = Time.unscaledTime + 1.5f;
        }

        // Speed of the friend from the two newest snapshots, m/s.
        public float Speed
        {
            get
            {
                if (buffer.Count < 2) return 0f;
                var a = buffer[buffer.Count - 2]; var b = buffer[buffer.Count - 1];
                return (b.s.pos - a.s.pos).magnitude / Mathf.Max(b.t - a.t, 0.01f);
            }
        }

        // Interpolated state at render time t (unscaled seconds).
        bool Sample(float t, out Vector3 pos, out Quaternion rot, out bool isYolk)
        {
            pos = default; rot = Quaternion.identity; isYolk = false;
            if (buffer.Count == 0) return false;
            int i = buffer.FindLastIndex(b => b.t <= t);
            EggState a = buffer[Mathf.Max(i, 0)].s, b2 = a;
            float k = 0f;
            if (i >= 0 && i + 1 < buffer.Count)
            {
                b2 = buffer[i + 1].s;
                k = Mathf.InverseLerp(buffer[i].t, buffer[i + 1].t, t);
            }
            pos = Vector3.Lerp(a.pos, b2.pos, k);
            rot = Quaternion.Slerp(a.rot, b2.rot, k);
            isYolk = b2.yolk;
            return true;
        }

        public void FixedStep()
        {
            if (!Sample(Time.unscaledTime - Delay, out var pos, out var rot, out yolkNow)) return;
            if (snapped) { body.position = pos; body.rotation = rot; root.transform.SetPositionAndRotation(pos, rot); snapped = false; }
            else { body.MovePosition(pos); body.MoveRotation(rot); }
        }

        public void Update(bool visible)
        {
            root.SetActive(visible && buffer.Count > 0);
            if (buffer.Count == 0) return;
            shell.SetActive(!yolkNow);
            yolk.SetActive(yolkNow);

            // Collide only with an intact friend, only if the host allows it, and not right after a teleport.
            bool want = Collide && !yolkNow;
            if (!want) col.enabled = false;
            else if (!col.enabled && Time.unscaledTime >= collideAfter) col.enabled = true;
            // The game's own "FallCancel" tag caps impact damage below the crack threshold.
            string tag = CrackOnHit ? "Untagged" : "FallCancel";
            if (col.tag != tag) col.tag = tag;

            int idx = buffer.FindLastIndex(b => b.t <= Time.unscaledTime - Delay - 0.2f);
            if (idx > 0) buffer.RemoveRange(0, idx);   // keep a little history behind the render time
        }

        public void Destroy()
        {
            Soften(col.GetInstanceID(), false);
            Object.Destroy(root);
        }

        // Our copied meshes carry the LOCAL player's skin material; swap it for the friend's.
        void ApplySkin(int id)
        {
            var target = SkinMaterial(id);
            var current = skin == int.MinValue ? localSkinMaterial : SkinMaterial(skin);
            skin = id;
            if (target == null || current == null || target == current) return;
            foreach (var r in shellRenderers)
            {
                var mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++) if (mats[m] == current) mats[m] = target;
                r.sharedMaterials = mats;
            }
        }

        static Material SkinMaterial(int id)
        {
            try
            {
                var data = id >= 0 ? Persistent.instance.GetAchievementData(id) : Persistent.instance.GetGuestAchievementData(id);
                return data != null ? data.shellMaterial : null;
            }
            catch { return null; }   // skin id unknown to this copy of the game
        }
    }

    public class GhostMover : MonoBehaviour
    {
        public Ghost ghost;
        void FixedUpdate() => ghost?.FixedStep();
    }
}
