using System.Collections.Generic;
using UnityEngine;

namespace EggCoop
{
    // A friend's egg: a visual copy of the local egg's shell (no colliders, no game scripts),
    // driven by snapshots ~100 ms in the past.
    public class Ghost
    {
        const float SnapDistance = 8f;
        public static float Delay = 0.1f;   // render this far in the past so there is always a next snapshot

        public string Nick = "";
        public float LastSeen;
        public Vector3 Position => root.transform.position;

        readonly GameObject root, shell, yolk;
        readonly List<Renderer> shellRenderers = new List<Renderer>();
        readonly List<(float t, EggState s)> buffer = new List<(float, EggState)>();
        int skin = int.MinValue;
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
            var core = World.instance.GetEggCore().transform;
            foreach (var mr in World.instance.GetEgg().GetShell().GetComponentsInChildren<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (!mr.enabled || mf == null || mf.sharedMesh == null) continue;
                var go = new GameObject(mr.name);
                go.transform.SetParent(shell.transform, false);
                go.transform.localPosition = core.InverseTransformPoint(mr.transform.position);
                go.transform.localRotation = Quaternion.Inverse(core.rotation) * mr.transform.rotation;
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
        }

        public void Push(EggState s)
        {
            float now = Time.unscaledTime;
            LastSeen = now;
            Nick = s.nick;
            if (buffer.Count > 0 && (buffer[buffer.Count - 1].s.pos - s.pos).sqrMagnitude > SnapDistance * SnapDistance) buffer.Clear();
            buffer.Add((now, s));
            if (buffer.Count > 30) buffer.RemoveAt(0);
            if (s.skin != skin) ApplySkin(s.skin);
        }

        public void Update(bool visible)
        {
            root.SetActive(visible && buffer.Count > 0);
            if (buffer.Count == 0) return;
            float t = Time.unscaledTime - Delay;
            int i = buffer.FindLastIndex(b => b.t <= t);
            EggState a = buffer[Mathf.Max(i, 0)].s, b2 = a;
            float k = 0f;
            if (i >= 0 && i + 1 < buffer.Count)
            {
                b2 = buffer[i + 1].s;
                k = Mathf.InverseLerp(buffer[i].t, buffer[i + 1].t, t);
            }
            root.transform.SetPositionAndRotation(Vector3.Lerp(a.pos, b2.pos, k), Quaternion.Slerp(a.rot, b2.rot, k));
            shell.SetActive(!b2.yolk);
            yolk.SetActive(b2.yolk);
            if (i > 0) buffer.RemoveRange(0, i);   // keep one snapshot behind the render time
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
}
