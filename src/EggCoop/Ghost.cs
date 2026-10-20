using System.Collections.Generic;
using UnityEngine;

namespace EggCoop
{
    // A friend's egg: a visual copy of the local egg's shell (no game scripts), driven by snapshots ~100 ms in the past.
    // With collisions on, it also carries a copy of the egg collider on a kinematic body, so the local egg can bump
    // into it and stand on it. The body is moved in FixedUpdate, which gives the physics engine real contact velocities.
    public class Ghost
    {
        const float SnapDistance = 8f;
        public static float Delay = 0.1f;   // render this far in the past so there is always a next snapshot
        public static bool Collide = true, CrackOnHit;   // the host's choice, see CoopNet.OnSettings

        public string Nick = "";
        public float LastSeen;
        public Vector3 Position => root.transform.position;

        readonly GameObject root, shell, yolk;
        readonly Rigidbody body;
        readonly Collider col;
        readonly List<Renderer> shellRenderers = new List<Renderer>();
        readonly List<(float t, EggState s)> buffer = new List<(float, EggState)>();
        int skin = int.MinValue;
        bool yolkNow, snapped = true;
        Material localSkinMaterial;

        public static bool CanBuild => World.instance != null && World.instance.GetEgg() != null
            && World.instance.GetEggCore() != null && !World.instance.GetEggCore().yolkState;

        public Ghost(int id)
        {
            root = new GameObject("EggCoop_Ghost_" + id);
            Object.DontDestroyOnLoad(root);
            shell = new GameObject("shell");
            shell.transform.SetParent(root.transform, false);

            // Copy every visible mesh of the local shell, positioned relative to the egg body.
            var core = World.instance.GetEggCore();
            var ct = core.transform;
            foreach (var mr in World.instance.GetEgg().GetShell().GetComponentsInChildren<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (!mr.enabled || mf == null || mf.sharedMesh == null) continue;
                var go = new GameObject(mr.name);
                go.transform.SetParent(shell.transform, false);
                go.transform.localPosition = ct.InverseTransformPoint(mr.transform.position);
                go.transform.localRotation = Quaternion.Inverse(ct.rotation) * mr.transform.rotation;
                go.transform.localScale = mr.transform.lossyScale;
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
            col.enabled = false;
            root.AddComponent<GhostMover>().ghost = this;
            Plugin.Log.LogInfo($"Ghost {id} body: {core.eggCollider.GetType().Name} {core.eggCollider.bounds.size}");
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

        public void Push(EggState s)
        {
            float now = Time.unscaledTime;
            LastSeen = now;
            Nick = s.nick;
            if (buffer.Count > 0 && (buffer[buffer.Count - 1].s.pos - s.pos).sqrMagnitude > SnapDistance * SnapDistance)
            {
                buffer.Clear();
                snapped = true;   // teleport: jump there without sweeping through anything on the way
            }
            buffer.Add((now, s));
            if (buffer.Count > 30) buffer.RemoveAt(0);
            if (s.skin != skin) ApplySkin(s.skin);
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

            // Collide only with an intact friend, only if the host allows it, and never switch the collider on while it
            // overlaps our egg (spawn points, teleports): the depenetration kick would launch us.
            bool want = Collide && !yolkNow;
            if (!want) col.enabled = false;
            else if (!col.enabled)
            {
                var mine = World.instance != null ? World.instance.GetEggCore() : null;
                // (a disabled collider reports empty bounds, so compare distance against two egg sizes instead)
                if (mine == null || mine.yolkState
                    || (mine.eggRigidbody.position - body.position).magnitude > 2f * mine.eggCollider.bounds.extents.magnitude)
                    col.enabled = true;
            }
            // The game's own "FallCancel" tag caps impact damage below the crack threshold.
            string tag = CrackOnHit ? "Untagged" : "FallCancel";
            if (col.tag != tag) col.tag = tag;

            int idx = buffer.FindLastIndex(b => b.t <= Time.unscaledTime - Delay - 0.2f);
            if (idx > 0) buffer.RemoveRange(0, idx);   // keep a little history behind the render time
        }

        public void Destroy() => Object.Destroy(root);

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
