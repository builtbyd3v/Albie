// WhistleSetup.cs — Forge/Pet/6. Add Whistle To Open Scene
//
// Adds the voice + whistle pieces to the gator in the CURRENTLY OPEN scene (no scene is created or saved):
//   * gator/Voice            AudioSource + StreamedPcmTextToSpeech (hub speech), wired into GatorAgentBridge.tts
//   * gator                  GatorTalkAnimator (head nod + pulse while speaking), GatorSummon, GatorFollowUp
//                            (keeps listening 15 s after an answer, then wanders off)
//   * WhistleButton          collider puck + Meta Interaction SDK PokeInteractable / RayInteractable, each with an
//                            InteractableUnityEventWrapper → WhistleButton.Press / HoverOn / HoverOff.
//                            XR (GatorMrukAdapter present): it becomes the template cloned onto every MRUK table.
// Idempotent: re-running reuses what exists and re-wires it. Interaction SDK types are looked up by name so this
// file compiles without the SDK (the button then works with mouse / F / controller B only).
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
        const string CapMaterialPath = "Assets/_Forge/Pet/Materials/M_Whistle.mat";
        const string BaseMaterialPath = "Assets/_Forge/Pet/Materials/M_Whistle_Base.mat";
        static readonly Vector3 ColliderSize = new Vector3(0.08f, 0.03f, 0.08f);
        const float FaceHeight = 0.016f;          // top of the cap above the button origin
        const float PokeSize = 0.07f;

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
            string isdk = AddInteractionSdk(button);
            AddSummon(root, button, voice);
            AddFollowUp(root, voice);
            var adapter = Object.FindAnyObjectByType<GatorMrukAdapter>();
            if (adapter != null) SetRef(adapter, "whistleTemplate", button);

            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = button.gameObject;
            Undo.CollapseUndoOperations(group);
            Debug.Log($"[WhistleSetup] Done in '{root.scene.name}': Voice + GatorTalkAnimator + GatorSummon + GatorFollowUp on '{root.name}', " +
                      $"WhistleButton ({isdk}{(adapter != null ? ", cloned onto every MRUK table" : "")}). Save the scene to keep it. Test: F / click the button / B.");
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

        /// A new whistle puck 0.3 m to the gator's right (it places itself on the desk at runtime), under `parent` (may be null).
        internal static WhistleButton CreateButton(GatorController gator, Transform parent)
        {
            var go = new GameObject("WhistleButton");
            if (parent != null) go.transform.SetParent(parent, false);
            Transform g = gator.transform;
            go.transform.position = g.position + g.right * 0.3f + Vector3.up * (ColliderSize.y * 0.5f);
            go.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(g.forward, Vector3.up).normalized, Vector3.up);

            var box = go.AddComponent<BoxCollider>();
            box.size = ColliderSize;
            box.isTrigger = true;
            var audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0.6f;

            Material baseMat = LoadOrCreateMaterial(BaseMaterialPath, new Color(0.16f, 0.17f, 0.2f));
            Material capMat = LoadOrCreateMaterial(CapMaterialPath, new Color(1f, 0.62f, 0.15f));
            Disc(go.transform, "Base", new Vector3(0.08f, 0.004f, 0.08f), new Vector3(0f, -0.008f, 0f), baseMat);
            Renderer cap = Disc(go.transform, "Cap", new Vector3(0.066f, 0.006f, 0.066f), new Vector3(0f, 0.004f, 0f), capMat);
            Disc(cap.transform.parent, "Dot", new Vector3(0.018f, 0.002f, 0.018f), new Vector3(0f, 0.011f, 0f), baseMat)
                .transform.SetParent(cap.transform, true);   // pressed with the cap

            var button = go.AddComponent<WhistleButton>();
            SetRef(button, "visual", cap);
            SetRef(button, "gator", gator);
            RegisterCreated(go, "Create WhistleButton");
            return button;
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

        internal static string AddInteractionSdk(WhistleButton button, bool warnNoInteractors = true)
        {
            Type poke = FindType("Oculus.Interaction.PokeInteractable");
            Type ray = FindType("Oculus.Interaction.RayInteractable");
            Type wrapper = FindType("Oculus.Interaction.InteractableUnityEventWrapper");
            Type plane = FindType("Oculus.Interaction.Surfaces.PlaneSurface");
            Type clipper = FindType("Oculus.Interaction.Surfaces.BoundsClipper");
            Type clipped = FindType("Oculus.Interaction.Surfaces.ClippedPlaneSurface");
            Type colliderSurface = FindType("Oculus.Interaction.Surfaces.ColliderSurface");
            if (poke == null || ray == null || wrapper == null || plane == null || clipper == null || clipped == null || colliderSurface == null)
            {
                Debug.LogWarning("[WhistleSetup] Meta Interaction SDK types not found; the button works with mouse / F / B only.");
                return "no Interaction SDK";
            }

            Transform root = button.transform;

            // Poke: PokeInteractable → ClippedPlaneSurface(PlaneSurface facing up + BoundsClipper) on the button face.
            GameObject pokeGo = Child(root, "Poke");
            GameObject surfaceGo = Child(pokeGo.transform, "Surface");
            surfaceGo.transform.localPosition = new Vector3(0f, FaceHeight, 0f);
            surfaceGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // PlaneSurface normal (-Z, "Backward") = button up
            Component planeC = Ensure(surfaceGo, plane);
            Component clipperC = Ensure(surfaceGo, clipper);
            Component clippedC = Ensure(surfaceGo, clipped);
            var clipSo = new SerializedObject(clipperC);
            SetVector(clipSo, "_size", new Vector3(PokeSize, PokeSize, 0.02f));
            clipSo.ApplyModifiedProperties();
            var clippedSo = new SerializedObject(clippedC);
            SetRef(clippedSo, "_planeSurface", planeC);
            SerializedProperty clippers = clippedSo.FindProperty("_clippers");
            if (clippers != null && clippers.isArray)
            {
                clippers.arraySize = 1;
                clippers.GetArrayElementAtIndex(0).objectReferenceValue = clipperC;
            }
            clippedSo.ApplyModifiedProperties();
            Component pokeC = Ensure(pokeGo, poke);
            SetRef(pokeC, "_surfacePatch", clippedC);
            WireWrapper(Ensure(pokeGo, wrapper), pokeC, button);

            // Ray: RayInteractable → ColliderSurface(button BoxCollider).
            GameObject rayGo = Child(root, "Ray");
            Component colliderSurfaceC = Ensure(rayGo, colliderSurface);
            SetRef(colliderSurfaceC, "_collider", button.GetComponent<BoxCollider>());
            Component rayC = Ensure(rayGo, ray);
            SetRef(rayC, "_surface", colliderSurfaceC);
            WireWrapper(Ensure(rayGo, wrapper), rayC, button);

            Type pokeInteractor = FindType("Oculus.Interaction.PokeInteractor");
            Type rayInteractor = FindType("Oculus.Interaction.RayInteractor");
            bool hasInteractors = !warnNoInteractors
                               || (pokeInteractor != null && Object.FindAnyObjectByType(pokeInteractor, FindObjectsInactive.Include) != null)
                               || (rayInteractor != null && Object.FindAnyObjectByType(rayInteractor, FindObjectsInactive.Include) != null);
            if (!hasInteractors)
                Debug.LogWarning("[WhistleSetup] No Poke/RayInteractor in this scene: add OVRComprehensiveInteractionRig " +
                                 "(com.meta.xr.sdk.interaction.ovr) to the camera rig for hand/controller presses. Mouse / F / B work regardless.");
            return "poke + ray";
        }

        static void WireWrapper(Component wrapper, Component interactable, WhistleButton button)
        {
            SetRef(wrapper, "_interactableView", interactable);
            AddListener(wrapper, "_whenSelect", button.Press);
            AddListener(wrapper, "_whenHover", button.HoverOn);
            AddListener(wrapper, "_whenUnhover", button.HoverOff);
            EditorUtility.SetDirty(wrapper);
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
