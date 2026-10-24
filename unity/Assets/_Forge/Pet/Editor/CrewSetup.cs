// CrewSetup.cs — Forge/Pet/7. Add Agent Crew To Open Scene
//
// Adds Bit's crew to the CURRENTLY OPEN scene (no scene is created or saved):
//   * GatorCrew        lead = the scene's gator, crewPrefab = its prefab (GatorAssistant), hats in M_Whistle (tinted)
//   * WakeListener     on the gator: always-on mic + VAD, say a gator's name and it listens (hub mode)
//   * GatorHatchery    the egg (near-left desk corner) + a small "next type" button, each with Meta Interaction SDK
//                      PokeInteractable / RayInteractable + InteractableUnityEventWrapper → Hatch / NextType
//                      (types looked up by name, so this compiles without the SDK; mouse and N always work)
// Idempotent: re-running reuses what exists and re-wires it. Save the scene yourself afterwards.
// -executeMethod Forge.Pet.Editor.CrewSetup.AddCrewToOpenScene

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Forge.Pet.Editor
{
    public static class CrewSetup
    {
        const string PrefabPath = "Assets/_Forge/Pet/Prefabs/GatorAssistant.prefab";
        const string HatMaterialPath = "Assets/_Forge/Pet/Materials/M_Whistle.mat";
        const string BaseMaterialPath = "Assets/_Forge/Pet/Materials/M_Whistle_Base.mat";
        static readonly Vector3 EggCollider = new Vector3(0.07f, 0.1f, 0.07f);     // root origin = collider centre
        static readonly Vector3 NextPos = new Vector3(0.06f, -0.04f, 0.015f);
        static readonly Vector3 NextCollider = new Vector3(0.03f, 0.02f, 0.03f);

        [MenuItem("Forge/Pet/7. Add Agent Crew To Open Scene", priority = 7)]
        public static void AddCrewToOpenScene()
        {
            GatorController lead = FindLead();
            if (lead == null)
            {
                Debug.LogError("[CrewSetup] No GatorController in the open scene. Open PetSandbox / PetSandboxXR / Main first.");
                return;
            }
            Undo.SetCurrentGroupName("Add Agent Crew To Open Scene");
            int group = Undo.GetCurrentGroup();

            GatorCrew crew = FindOrCreateCrew(lead);
            if (lead.GetComponent<WakeListener>() == null) Undo.AddComponent<WakeListener>(lead.gameObject);
            GatorHatchery egg = FindOrCreateHatchery(lead);
            string isdk = AddInteraction(egg);

            EditorSceneManager.MarkSceneDirty(lead.gameObject.scene);
            Selection.activeGameObject = egg.gameObject;
            Undo.CollapseUndoOperations(group);
            Debug.Log($"[CrewSetup] Done in '{lead.gameObject.scene.name}': {crew.name} (lead '{lead.name}', WakeListener) + GatorHatchery ({isdk}). " +
                      "Save the scene to keep it. Test: press the egg / click it / N to hatch, the arrow to pick a type, X to send the last one home.");
        }

        static GatorController FindLead()
        {
            foreach (var g in Object.FindObjectsByType<GatorController>(FindObjectsInactive.Include))
                if (g.GetComponent<GatorCrewMember>() == null) return g;
            return null;
        }

        static GatorCrew FindOrCreateCrew(GatorController lead)
        {
            var crew = Object.FindAnyObjectByType<GatorCrew>(FindObjectsInactive.Include);
            if (crew == null)
            {
                var go = new GameObject("GatorCrew");
                Undo.RegisterCreatedObjectUndo(go, "Create GatorCrew");
                crew = Undo.AddComponent<GatorCrew>(go);
            }
            var prefab = PrefabUtility.GetCorrespondingObjectFromSource(lead.gameObject);
            if (prefab == null) prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            WhistleSetup.SetRef(crew, "lead", lead);
            if (prefab != null) WhistleSetup.SetRef(crew, "crewPrefab", prefab);
            else Debug.LogWarning("[CrewSetup] GatorAssistant prefab not found; crew gators will be copies of the scene's gator.");
            var hat = WhistleSetup.LoadOrCreateMaterial(HatMaterialPath, new Color(1f, 0.62f, 0.15f));
            if (hat != null) WhistleSetup.SetRef(crew, "hatMaterial", hat);
            return crew;
        }

        static GatorHatchery FindOrCreateHatchery(GatorController lead)
        {
            var existing = Object.FindAnyObjectByType<GatorHatchery>(FindObjectsInactive.Include);
            if (existing != null) return existing;

            var go = new GameObject("GatorHatchery");
            Transform g = lead.transform;
            go.transform.position = g.position - g.right * 0.3f + Vector3.up * (EggCollider.y * 0.5f);
            go.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(g.forward, Vector3.up).normalized, Vector3.up);
            var box = go.AddComponent<BoxCollider>();
            box.size = EggCollider;
            box.isTrigger = true;

            Material baseMat = WhistleSetup.LoadOrCreateMaterial(BaseMaterialPath, new Color(0.16f, 0.17f, 0.2f));
            Material eggMat = WhistleSetup.LoadOrCreateMaterial(HatMaterialPath, new Color(1f, 0.62f, 0.15f));
            float bottom = -EggCollider.y * 0.5f;
            WhistleSetup.Disc(go.transform, "Base", new Vector3(0.075f, 0.004f, 0.075f), new Vector3(0f, bottom + 0.004f, 0f), baseMat);
            Renderer egg = Primitive(go.transform, PrimitiveType.Sphere, "Egg", new Vector3(0.05f, 0.066f, 0.05f), new Vector3(0f, bottom + 0.008f + 0.033f, 0f), eggMat);

            var next = new GameObject("NextButton");
            next.transform.SetParent(go.transform, false);
            next.transform.localPosition = NextPos;
            var nextBox = next.AddComponent<BoxCollider>();
            nextBox.size = NextCollider;
            nextBox.isTrigger = true;
            WhistleSetup.Disc(next.transform, "Base", new Vector3(0.03f, 0.004f, 0.03f), new Vector3(0f, -0.006f, 0f), baseMat);
            Renderer arrow = Primitive(next.transform, PrimitiveType.Cube, "Arrow", new Vector3(0.012f, 0.006f, 0.012f), new Vector3(0f, 0.001f, 0f), eggMat);
            arrow.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);

            var hatchery = go.AddComponent<GatorHatchery>();
            WhistleSetup.SetRef(hatchery, "egg", egg);
            WhistleSetup.SetRef(hatchery, "nextButton", nextBox);
            Undo.RegisterCreatedObjectUndo(go, "Create GatorHatchery");
            return hatchery;
        }

        static Renderer Primitive(Transform parent, PrimitiveType shape, string name, Vector3 scale, Vector3 localPos, Material mat)
        {
            GameObject p = GameObject.CreatePrimitive(shape);
            p.name = name;
            Object.DestroyImmediate(p.GetComponent<Collider>());
            p.transform.SetParent(parent, false);
            p.transform.localPosition = localPos;
            p.transform.localScale = scale;
            var r = p.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        // ------------------------------------------------------------------ Interaction SDK (by name)

        static string AddInteraction(GatorHatchery egg)
        {
            Type wrapper = WhistleSetup.FindType("Oculus.Interaction.InteractableUnityEventWrapper");
            if (wrapper == null || WhistleSetup.FindType("Oculus.Interaction.PokeInteractable") == null)
            {
                Debug.LogWarning("[CrewSetup] Meta Interaction SDK types not found; the egg works with mouse / N only.");
                return "no Interaction SDK";
            }
            Transform root = egg.transform;
            float eggTop = -EggCollider.y * 0.5f + 0.008f + 0.066f;
            PokeAndRay(root, root.GetComponent<BoxCollider>(), eggTop, 0.05f, egg.Hatch, egg.HoverOn, egg.HoverOff);
            Transform next = root.Find("NextButton");
            if (next != null) PokeAndRay(next, next.GetComponent<BoxCollider>(), 0.004f, 0.03f, egg.NextType, null, null);
            return "poke + ray";
        }

        // Same shape as WhistleSetup.AddInteractionSdk: Poke = PokeInteractable on a clipped plane facing up at faceY,
        // Ray = RayInteractable on the collider; both through an InteractableUnityEventWrapper.
        static void PokeAndRay(Transform root, BoxCollider col, float faceY, float pokeSize, UnityAction select, UnityAction hover, UnityAction unhover)
        {
            Type poke = WhistleSetup.FindType("Oculus.Interaction.PokeInteractable");
            Type ray = WhistleSetup.FindType("Oculus.Interaction.RayInteractable");
            Type wrapper = WhistleSetup.FindType("Oculus.Interaction.InteractableUnityEventWrapper");
            Type plane = WhistleSetup.FindType("Oculus.Interaction.Surfaces.PlaneSurface");
            Type clipper = WhistleSetup.FindType("Oculus.Interaction.Surfaces.BoundsClipper");
            Type clipped = WhistleSetup.FindType("Oculus.Interaction.Surfaces.ClippedPlaneSurface");
            Type colliderSurface = WhistleSetup.FindType("Oculus.Interaction.Surfaces.ColliderSurface");
            if (poke == null || ray == null || wrapper == null || plane == null || clipper == null || clipped == null || colliderSurface == null) return;

            GameObject pokeGo = WhistleSetup.Child(root, "Poke");
            GameObject surfaceGo = WhistleSetup.Child(pokeGo.transform, "Surface");
            surfaceGo.transform.localPosition = new Vector3(0f, faceY, 0f);
            surfaceGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Component planeC = WhistleSetup.Ensure(surfaceGo, plane);
            Component clipperC = WhistleSetup.Ensure(surfaceGo, clipper);
            Component clippedC = WhistleSetup.Ensure(surfaceGo, clipped);
            var clipSo = new SerializedObject(clipperC);
            WhistleSetup.SetVector(clipSo, "_size", new Vector3(pokeSize, pokeSize, 0.02f));
            clipSo.ApplyModifiedProperties();
            var clippedSo = new SerializedObject(clippedC);
            WhistleSetup.SetRef(clippedSo, "_planeSurface", planeC);
            SerializedProperty clippers = clippedSo.FindProperty("_clippers");
            if (clippers != null && clippers.isArray)
            {
                clippers.arraySize = 1;
                clippers.GetArrayElementAtIndex(0).objectReferenceValue = clipperC;
            }
            clippedSo.ApplyModifiedProperties();
            Component pokeC = WhistleSetup.Ensure(pokeGo, poke);
            WhistleSetup.SetRef(pokeC, "_surfacePatch", clippedC);
            Wire(WhistleSetup.Ensure(pokeGo, wrapper), pokeC, select, hover, unhover);

            GameObject rayGo = WhistleSetup.Child(root, "Ray");
            Component colliderSurfaceC = WhistleSetup.Ensure(rayGo, colliderSurface);
            WhistleSetup.SetRef(colliderSurfaceC, "_collider", col);
            Component rayC = WhistleSetup.Ensure(rayGo, ray);
            WhistleSetup.SetRef(rayC, "_surface", colliderSurfaceC);
            Wire(WhistleSetup.Ensure(rayGo, wrapper), rayC, select, hover, unhover);
        }

        static void Wire(Component wrapper, Component interactable, UnityAction select, UnityAction hover, UnityAction unhover)
        {
            WhistleSetup.SetRef(wrapper, "_interactableView", interactable);
            WhistleSetup.AddListener(wrapper, "_whenSelect", select);
            if (hover != null) WhistleSetup.AddListener(wrapper, "_whenHover", hover);
            if (unhover != null) WhistleSetup.AddListener(wrapper, "_whenUnhover", unhover);
            EditorUtility.SetDirty(wrapper);
        }
    }
}
