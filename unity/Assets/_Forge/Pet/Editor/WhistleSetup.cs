// WhistleSetup.cs — Forge/Pet/6. Add Whistle To Open Scene
//
// Adds the voice + whistle pieces to the gator in the CURRENTLY OPEN scene (no scene is created or saved):
//   * gator/Voice            AudioSource + StreamedPcmTextToSpeech (hub speech), wired into GatorAgentBridge.tts
//   * gator                  GatorTalkAnimator (head nod + pulse while speaking), GatorSummon, GatorFollowUp
//                            (keeps listening 15 s after an answer, then wanders off)
//   * WhistleButton          Hudson's floating dock ring (Core DockRing.Create, white whistle icon set at runtime)
//                            whose uGUI Button presses it, plus Interaction SDK ray + poke canvas interaction (the SDK's
//                            QuickActions, as ArrivalSetup does for START). An old puck's collider / Poke / Ray go away.
//                            XR (GatorMrukAdapter present): it becomes the template cloned onto every MRUK table.
// Idempotent: re-running reuses what exists and re-wires it. Interaction SDK types are looked up by name so this
// file compiles without the SDK (the ring then works with mouse / F / controller B only).
// Save the scene yourself afterwards. -executeMethod Forge.Pet.Editor.WhistleSetup.AddWhistleToOpenScene
// (The internal helpers are shared with CrewSetup and PetRuntimeSetup, which builds the same pieces into
// Prefabs/PetRuntime.prefab inside a preview scene with UseUndo = false.)

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Forge.Pet.Editor
{
    public static class WhistleSetup
    {
        /// Menu commands record Undo; PetRuntimeSetup turns this off while it builds inside a preview scene.
        internal static bool UseUndo = true;

        [MenuItem("Forge/Pet/6. Add Whistle To Open Scene", priority = 6)]
        public static void AddWhistleToOpenScene()
        {
            var gator = Object.FindAnyObjectByType<GatorController>();
            if (gator == null)
            {
                Debug.LogError("[WhistleSetup] No GatorController in the open scene. Open PetSandbox / PetSandboxXR (or a scene with GatorAssistant) first.");
                return;
            }
            Undo.SetCurrentGroupName("Add Whistle To Open Scene");
            int group = Undo.GetCurrentGroup();

            GameObject root = gator.gameObject;
            StreamedPcmTextToSpeech voice = AddVoice(root);
            AddTalkAnimator(root, voice);
            WhistleButton button = FindOrCreateButton(gator);
            string isdk = AddInteractionSdk(button);   // also turns an old puck into the ring
            AddSummon(root, button, voice);
            AddFollowUp(root, voice);
            var adapter = Object.FindAnyObjectByType<GatorMrukAdapter>();
            if (adapter != null) SetRef(adapter, "whistleTemplate", button);

            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = button.gameObject;
            Undo.CollapseUndoOperations(group);
            Debug.Log($"[WhistleSetup] Done in '{root.scene.name}': Voice + GatorTalkAnimator + GatorSummon + GatorFollowUp on '{root.name}', " +
                      $"WhistleButton ({isdk}{(adapter != null ? ", cloned onto every MRUK table" : "")}). Save the scene to keep it. Test: F / click the ring / B.");
        }

        // ------------------------------------------------------------------ voice

        internal static StreamedPcmTextToSpeech AddVoice(GameObject gator)
        {
            var voice = gator.GetComponentInChildren<StreamedPcmTextToSpeech>(true);
            if (voice == null)
            {
                var go = new GameObject("Voice");
                RegisterCreated(go, "Add Voice");
                go.transform.SetParent(gator.transform, false);
                go.transform.position = HeadPosition(gator);
                voice = Add<StreamedPcmTextToSpeech>(go);   // RequireComponent adds the AudioSource
                var src = go.GetComponent<AudioSource>();
                if (src != null) { src.playOnAwake = false; src.spatialBlend = 1f; }
            }

            var bridge = gator.GetComponent<GatorAgentBridge>();
            if (bridge == null) bridge = Object.FindAnyObjectByType<GatorAgentBridge>();
            if (bridge != null) SetRef(bridge, "tts", voice);
            else Debug.LogWarning("[WhistleSetup] No GatorAgentBridge found; it will auto-find the Voice child at runtime.");
            return voice;
        }

        internal static void AddTalkAnimator(GameObject gator, StreamedPcmTextToSpeech voice)
        {
            var talk = gator.GetComponent<GatorTalkAnimator>();
            if (talk == null) talk = Add<GatorTalkAnimator>(gator);
            SetRef(talk, "tts", voice);
            Animator animator = gator.GetComponentInChildren<Animator>(true);
            Transform head = animator != null ? FindChild(animator.transform, "head") : null;
            if (head != null) SetRef(talk, "headBone", head);
            else Debug.LogWarning("[WhistleSetup] No 'head' bone under the gator's Animator; GatorTalkAnimator will only pulse.");
            Transform jaw = animator != null ? FindChild(animator.transform, "jaw") : null;   // PR 26's Gator_Model.fbx
            if (jaw != null) SetRef(talk, "jawBone", jaw);
            else Debug.Log("[WhistleSetup] No 'jaw' bone (PR 26 not merged?): the mouth won't move while Bit speaks.");
        }

        internal static void AddSummon(GameObject gator, WhistleButton button, StreamedPcmTextToSpeech voice)
        {
            var summon = gator.GetComponent<GatorSummon>();
            if (summon == null) summon = Add<GatorSummon>(gator);
            var ptt = gator.GetComponent<PushToTalk>();
            if (ptt == null) ptt = Object.FindAnyObjectByType<PushToTalk>();
            if (ptt == null) Debug.LogWarning("[WhistleSetup] No PushToTalk in the scene; the whistle can't listen without it.");
            SetRef(summon, "button", button);
            SetRef(summon, "controller", gator.GetComponent<GatorController>());
            if (ptt != null) SetRef(summon, "pushToTalk", ptt);
            SetRef(summon, "tts", voice);
            SetRef(summon, "sfxSource", button.GetComponent<AudioSource>());
        }

        internal static void AddFollowUp(GameObject gator, StreamedPcmTextToSpeech voice)
        {
            var follow = gator.GetComponent<GatorFollowUp>();
            if (follow == null) follow = Add<GatorFollowUp>(gator);
            SetRef(follow, "tts", voice);
            var ptt = gator.GetComponent<PushToTalk>();
            if (ptt == null) ptt = Object.FindAnyObjectByType<PushToTalk>();
            if (ptt != null) SetRef(follow, "pushToTalk", ptt);
            SetRef(follow, "controller", gator.GetComponent<GatorController>());
        }

        // ------------------------------------------------------------------ button

        static WhistleButton FindOrCreateButton(GatorController gator)
        {
            var existing = Object.FindAnyObjectByType<WhistleButton>(FindObjectsInactive.Include);
            return existing != null ? existing : CreateButton(gator, null);
        }

        /// A new whistle ring 0.3 m to the gator's right (it places itself beside the dock at runtime), under `parent` (may be null).
        internal static WhistleButton CreateButton(GatorController gator, Transform parent)
        {
            var go = new GameObject("WhistleButton");
            if (parent != null) go.transform.SetParent(parent, false);
            Transform g = gator.transform;
            go.transform.position = g.position + g.right * 0.3f;
            go.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.ProjectOnPlane(g.forward, Vector3.up).normalized);   // flat, like the dock
            var audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0.6f;

            var button = go.AddComponent<WhistleButton>();
            EnsureRing(button);
            SetRef(button, "gator", gator);
            RegisterCreated(go, "Create WhistleButton");
            return button;
        }

        // The button's DockRing (made if missing) wired into it; what's left of an old puck (collider, Cap / Base, Poke / Ray) removed.
        static DockRing EnsureRing(WhistleButton button)
        {
            Transform root = button.transform;
            foreach (string old in new[] { "Base", "Cap", "Poke", "Ray" })
            {
                Transform t = root.Find(old);
                if (t != null) Remove(t.gameObject);
            }
            var box = button.GetComponent<BoxCollider>();
            if (box != null) { if (UseUndo) Undo.DestroyObjectImmediate(box); else Object.DestroyImmediate(box); }

            DockRing ring = button.GetComponentInChildren<DockRing>(true);
            if (ring == null)
            {
                ring = DockRing.Create(root, WhistleButton.Label, ForgeTheme.Tool, ForgeTheme.DockModeRing, null);
                RegisterCreated(ring.gameObject, "Create whistle ring");
            }
            SetRef(button, "ring", ring);
            return ring;
        }

        static void Remove(GameObject go)
        {
            if (UseUndo) Undo.DestroyObjectImmediate(go);
            else Object.DestroyImmediate(go);
        }

        internal static Renderer Disc(Transform parent, string name, Vector3 scale, Vector3 localPos, Material mat)
        {
            GameObject d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);   // 2 units tall: scale.y = half height
            d.name = name;
            Object.DestroyImmediate(d.GetComponent<Collider>());
            d.transform.SetParent(parent, false);
            d.transform.localPosition = localPos;
            d.transform.localScale = scale;
            var r = d.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        internal static Material LoadOrCreateMaterial(string path, Color color)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;
            mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.35f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        // ------------------------------------------------------------------ Interaction SDK (by name)

        /// Ray + poke canvas interaction on the whistle's ring (ArrivalSetup.AddCanvasInteraction, the SDK's QuickActions).
        internal static string AddInteractionSdk(WhistleButton button, bool warnNoInteractors = true)
        {
            DockRing ring = EnsureRing(button);
            string result = ArrivalSetup.AddCanvasInteraction(ring.gameObject, "whistle ring");
            Type pokeInteractor = FindType("Oculus.Interaction.PokeInteractor");
            Type rayInteractor = FindType("Oculus.Interaction.RayInteractor");
            bool hasInteractors = !warnNoInteractors
                               || (pokeInteractor != null && Object.FindAnyObjectByType(pokeInteractor, FindObjectsInactive.Include) != null)
                               || (rayInteractor != null && Object.FindAnyObjectByType(rayInteractor, FindObjectsInactive.Include) != null);
            if (!hasInteractors)
                Debug.LogWarning("[WhistleSetup] No Poke/RayInteractor in this scene: add OVRComprehensiveInteractionRig " +
                                 "(com.meta.xr.sdk.interaction.ovr) to the camera rig for hand/controller presses. Mouse / F / B work regardless.");
            return result;
        }

        internal static void AddListener(Component wrapper, string fieldName, UnityAction call)
        {
            FieldInfo field = wrapper.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null || !typeof(UnityEvent).IsAssignableFrom(field.FieldType))
            {
                Debug.LogWarning($"[WhistleSetup] {wrapper.GetType().Name}.{fieldName} not found; wire it by hand.");
                return;
            }
            if (UseUndo) Undo.RecordObject(wrapper, "Wire whistle event");
            var evt = (UnityEvent)field.GetValue(wrapper);
            if (evt == null) { evt = new UnityEvent(); field.SetValue(wrapper, evt); }
            for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--)   // idempotent: drop our old listener
                if (evt.GetPersistentTarget(i) == (Object)call.Target && evt.GetPersistentMethodName(i) == call.Method.Name)
                    UnityEventTools.RemovePersistentListener(evt, i);
            UnityEventTools.AddVoidPersistentListener(evt, call);
        }

        // ------------------------------------------------------------------ helpers

        static Vector3 HeadPosition(GameObject gator)
        {
            Renderer[] rs = gator.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return gator.transform.position + Vector3.up * 0.2f;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            // Front-top of the body: roughly where the head is (gator faces +Z).
            return b.center + gator.transform.forward * (b.extents.z * 0.6f) + Vector3.up * (b.extents.y * 0.4f);
        }

        internal static GameObject Child(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            RegisterCreated(go, "Create " + name);
            go.transform.SetParent(parent, false);
            return go;
        }

        internal static Component Ensure(GameObject go, Type t)
        {
            Component c = go.GetComponent(t);
            if (c != null) return c;
            return UseUndo ? Undo.AddComponent(go, t) : go.AddComponent(t);
        }

        internal static T Add<T>(GameObject go) where T : Component => UseUndo ? Undo.AddComponent<T>(go) : go.AddComponent<T>();

        internal static void RegisterCreated(GameObject go, string undoName)
        {
            if (UseUndo) Undo.RegisterCreatedObjectUndo(go, undoName);
        }

        internal static Type FindType(string fullName)
        {
            foreach (Type t in TypeCache.GetTypesDerivedFrom<Component>())
                if (t.FullName == fullName && !t.IsAbstract) return t;
            return null;
        }

        internal static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindChild(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        internal static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            SetRef(so, field, value);
            so.ApplyModifiedProperties();
        }

        internal static void SetRef(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null || p.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogWarning($"[WhistleSetup] {so.targetObject.GetType().Name} has no object field '{field}'.");
                return;
            }
            p.objectReferenceValue = value;
        }

        internal static void SetVector(SerializedObject so, string field, Vector3 value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null && p.propertyType == SerializedPropertyType.Vector3) p.vector3Value = value;
            else Debug.LogWarning($"[WhistleSetup] {so.targetObject.GetType().Name} has no Vector3 field '{field}'.");
        }
    }
}
