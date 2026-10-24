// GatorSetup.cs — reproducible editor setup for the AI-assistant gator pet ("Bit").
//
// This file IS the reproducibility story: every generated asset under
// Assets/_Forge/Pet/{Materials,Animation,Prefabs,Scenes,Mocks}/ is produced from here and can be
// regenerated at any time. Every step is idempotent: re-running overwrites the generated assets in
// place (keeping their GUIDs where Unity allows, so references from other scenes/prefabs survive).
//
// Menu:            Forge/Pet/...
// -executeMethod:  Forge.Pet.Editor.GatorSetup.FixMaterial | BuildAnimator | BuildPrefab |
//                  BuildSandboxScene | BuildXRSandboxScene | BuildAll
//                  (batch + quit: Forge.Pet.Editor.GatorSetupCli.BuildAllAndQuit)
//
// Scenes:
//   PetSandbox.unity    desktop only (DesktopRig, primitives); runtime NavMesh via GatorSandboxNav.
//   PetSandboxXR.unity  mirrors Core's MainSceneBuilder (passthrough OVRCameraRig + MRUK + DeskLocator) plus
//                       the gator; DeskLocator.deskFound -> GatorMrukAdapter.OnDeskFound builds the NavMesh.
// No AI Navigation package in the team project: NavMeshes are built at runtime from box sources by
// Forge.Pet.GatorNavSurface (built-in UnityEngine.AI only). Nothing is baked or saved as an asset.
//
// Source art (NEVER modified, only referenced / copied from):
//   Assets/_Forge/Pet/ThirdParty/Quirky Series/Ultimate Pack Vol.1/Mega Pack Vol.4/River Vol.2/
//
// Facts read from the imported asset files (Unity 6000.6.7f1):
//   * Prefabs/Alligator.prefab root "Alligator" holds: LODGroup (4 LODs, size 1.6275 m),
//     Animator (Generic, NO Avatar, controller = pack's AC_Alligator, applyRootMotion 0,
//     cullingMode AlwaysAnimate) and its own CapsuleCollider (r 0.133, h 1.414, dir Z).
//     Children: "Rig" (rot -90 X, scale 100; bones root/body/head/leg.*/tail.*) and
//     "Mesh_LOD0..3" (SkinnedMeshRenderers, scale 100, material M_Alligator).
//     -> The Animator lives on the model root, i.e. on "GatorAssistant/Model" in our prefab.
//   * Models/*.fbx: globalScale 1, useFileScale 1 (cm-authored FBX, hence the 100x child scales).
//     Native model size ≈ 0.49 (x, width) × 0.59 (y, height) × 1.63 (z, length) m, feet at y≈0,
//     facing +Z.
//   * Animations/Alligator_Animations.fbx: Generic, clips Attack, Bounce, Clicked, Death, Eat, Fear,
//     Fly, Hit, Idle_A, Idle_B, Idle_C, Jump, Lay, Roll, Run, Sit, Spin, Swim, Walk (all loopTime 1
//     except Death). Clips are sub-assets of the FBX.
//   * Materials/M_Alligator.mat: built-in RP "Toon/SoftSurface": _MainTex (tex), _Color (0.4 grey),
//     _Emission (float 0.5).
//   * _Shader/URP (Unity 2019.3.0f3 and above)/SoftSurfaceGraph.shadergraph (legacy PBR master
//     graph) exposes the SAME reference names: _Color (Color), _MainTex (Texture2D),
//     _Emission (Vector1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using EffectMesh = Meta.XR.MRUtilityKit.EffectMesh;
using MRUKAnchor = Meta.XR.MRUtilityKit.MRUKAnchor;

namespace Forge.Pet.Editor
{
    public static class GatorSetup
    {
        // ------------------------------------------------------------------------------------
        // Paths (match docs/PET_AGENT_CONTRACT.md)
        // ------------------------------------------------------------------------------------
        const string QuirkyDir = "Assets/_Forge/Pet/ThirdParty/Quirky Series";
        const string PackDir = QuirkyDir + "/Ultimate Pack Vol.1/Mega Pack Vol.4/River Vol.2";
        const string SrcPrefabPath = PackDir + "/Prefabs/Alligator.prefab";
        const string SrcMaterialPath = PackDir + "/Materials/M_Alligator.mat";
        const string SrcAnimFbxPath = PackDir + "/Animations/Alligator_Animations.fbx";
        const string UrpShaderGraphPath = QuirkyDir + "/_Shader/URP (Unity 2019.3.0f3 and above)/SoftSurfaceGraph.shadergraph";
        const string UrpLitShaderName = "Universal Render Pipeline/Lit";

        const string PetRoot = "Assets/_Forge/Pet";
        const string MaterialsDir = PetRoot + "/Materials";
        const string AnimationDir = PetRoot + "/Animation";
        const string PrefabsDir = PetRoot + "/Prefabs";
        const string ScenesDir = PetRoot + "/Scenes";
        const string MocksDir = PetRoot + "/Mocks";

        public const string GatorMaterialPath = MaterialsDir + "/M_Gator_URP.mat";
        public const string FloorMaterialPath = MaterialsDir + "/M_Sandbox_Floor.mat";
        public const string PropMaterialPath = MaterialsDir + "/M_Sandbox_Prop.mat";
        public const string DeskMaterialPath = MaterialsDir + "/M_Sandbox_Desk.mat";
        public const string ControllerPath = AnimationDir + "/AC_Gator.controller";
        public const string PrefabPath = PrefabsDir + "/GatorAssistant.prefab";
        public const string ScenePath = ScenesDir + "/PetSandbox.unity";
        public const string XRScenePath = ScenesDir + "/PetSandboxXR.unity";
        /// Baked by the old staging version (AI Navigation); deleted if found so nothing references it.
        const string LegacyNavMeshDataDir = ScenesDir + "/PetSandbox";
        const string LegacyNavMeshDataPath = LegacyNavMeshDataDir + "/NavMesh-Navigation.asset";

        // Meta XR assets: copied verbatim from Assets/_Forge/Core/Editor/MainSceneBuilder.cs (Hudson).
        const string CameraRigPath = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string MrukPath = "Packages/com.meta.xr.mrutilitykit/Core/Tools/MRUK.prefab";
        const string TableMaterialPath = "Packages/com.meta.xr.mrutilitykit/Core/Materials/BLUE_Table.mat";
        public const string IntroFlowPath = MocksDir + "/intro_flow.json";

        // ------------------------------------------------------------------------------------
        // Tuning
        // ------------------------------------------------------------------------------------
        /// Target standing height of the gator in metres ("knee-high small creature").
        /// Sandbox scenes are play-mode only; Build Settings (ProjectSettings/) belong to Core/Hudson. Opt in to ship one.
        static bool AddScenesToBuildSettings => false;
        const float TargetHeight = 0.12f;   // hamster-sized: shares the desk with someone working. 0.45 and 0.22 both read as "too big".
        const float ReferenceScale = 0.761f; // model scale at which walkSpeed 0.6 / runSpeed 1.8 m/s matched the stride

        /// Fallback native bounds if renderer bounds cannot be measured in the editor
        /// (values computed from the SkinnedMeshRenderer AABB in Alligator.prefab).
        static readonly Vector3 FallbackNativeCenter = new Vector3(-0.0013f, 0.296f, 0.0045f);
        static readonly Vector3 FallbackNativeSize = new Vector3(0.487f, 0.592f, 1.628f);

        /// NavMeshAgent on the prefab. agentTypeID stays 0 (Humanoid) in the asset: GatorController sets
        /// GatorNavSurface.GatorAgentTypeId (created at runtime via NavMesh.CreateSettings) in Awake.
        const float AgentRadius = 0.06f;   // must match GatorNavSurface's agent type (radius/height)
        const float AgentHeight = 0.2f;

        /// Desktop sandbox floor: Unity's Plane is 10x10 m at scale 1.
        const float FloorScale = 2f;
        const float FloorSize = 10f * FloorScale;

        /// True if the last BuildAll() finished every step without an error (read by GatorSetupCli).
        public static bool LastBuildSucceeded { get; private set; }

        // Runtime component types, resolved by full name so a missing/renamed class logs a warning
        // instead of breaking compilation of this editor script (other agents write them in parallel).
        static readonly string[] PrefabRuntimeComponents =
        {
            "Forge.Pet.GatorController",
            "Forge.Pet.GatorExpressions",
            "Forge.Pet.GatorInteraction",
            "Forge.Pet.GatorCaption",
            "Forge.Pet.GatorAgentBridge",
            "Forge.Pet.PushToTalk",
            "Forge.Pet.TypedSpeechToText",
            "Forge.Pet.MockEventSource",
            "Forge.Pet.AgentClient",
            MrukAdapterType, // self-sufficient: falls back to MRUK's scene-loaded callback when no DeskLocator is wired
        };

        const string ControllerType = "Forge.Pet.GatorController";
        const string MrukAdapterType = "Forge.Pet.GatorMrukAdapter";
        const string NavSurfaceType = "Forge.Pet.GatorNavSurface";
        const string SandboxNavType = "Forge.Pet.GatorSandboxNav";

        // ====================================================================================
        // Menu items / -executeMethod entry points
        // ====================================================================================

        [MenuItem("Forge/Pet/1. Fix Material (URP)", priority = 1)]
        public static void FixMaterial() => Run("FixMaterial", () => FixMaterialInternal());

        [MenuItem("Forge/Pet/2. Build Animator", priority = 2)]
        public static void BuildAnimator() => Run("BuildAnimator", () => BuildAnimatorInternal());

        [MenuItem("Forge/Pet/3. Build Prefab", priority = 3)]
        public static void BuildPrefab() => Run("BuildPrefab", () => BuildPrefabInternal());

        [MenuItem("Forge/Pet/4. Build Sandbox Scene", priority = 4)]
        public static void BuildSandboxScene() => Run("BuildSandboxScene", () => BuildSandboxSceneInternal());

        [MenuItem("Forge/Pet/5. Build XR Sandbox Scene (Simulator)", priority = 5)]
        public static void BuildXRSandboxScene() => Run("BuildXRSandboxScene", () => BuildXRSandboxSceneInternal());

        [MenuItem("Forge/Pet/Build All", priority = 20)]
        public static void BuildAll()
        {
            var summary = new StringBuilder("[GatorSetup] BuildAll summary:\n");
            bool ok = true;
            ok &= Run("FixMaterial", () => summary.AppendLine("  material : " + FixMaterialInternal()));
            ok &= Run("BuildAnimator", () => summary.AppendLine("  animator : " + BuildAnimatorInternal()));
            ok &= Run("BuildPrefab", () => summary.AppendLine("  prefab   : " + BuildPrefabInternal()));
            ok &= Run("BuildSandboxScene", () => summary.AppendLine("  scene    : " + BuildSandboxSceneInternal()));
            ok &= Run("BuildXRSandboxScene", () => summary.AppendLine("  xr scene : " + BuildXRSandboxSceneInternal()));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            LastBuildSucceeded = ok;
            summary.AppendLine(ok ? "  RESULT: OK" : "  RESULT: FAILED (see errors above)");
            if (ok) Debug.Log(summary.ToString()); else Debug.LogError(summary.ToString());
        }

        /// Runs one step with clear error logging. Returns false on exception.
        static bool Run(string step, Action action)
        {
            try
            {
                action();
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GatorSetup] {step} FAILED: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        // ====================================================================================
        // 1. Material: M_Alligator (built-in RP) -> M_Gator_URP (pack's URP SoftSurfaceGraph)
        // ====================================================================================

        static string FixMaterialInternal()
        {
            EnsureFolder(MaterialsDir);

            var src = AssetDatabase.LoadAssetAtPath<Material>(SrcMaterialPath);
            if (src == null) throw new FileNotFoundException("Source material not found", SrcMaterialPath);

            // Read the source values straight from the built-in material (property names verified
            // in M_Alligator.mat / SoftSurface.shader).
            Texture mainTex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            Color color = src.HasProperty("_Color") ? src.GetColor("_Color") : new Color(0.4f, 0.4f, 0.4f, 1f);
            float emission = src.HasProperty("_Emission") ? src.GetFloat("_Emission") : 0.5f;

            // Prefer the pack's URP Shader Graph. Its .meta was shipped with DefaultImporter, so if it
            // does not load as a Shader yet, force a reimport (Shader Graph's importer then claims it).
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(UrpShaderGraphPath);
            if (shader == null && File.Exists(UrpShaderGraphPath))
            {
                AssetDatabase.ImportAsset(UrpShaderGraphPath, ImportAssetOptions.ForceUpdate);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(UrpShaderGraphPath);
            }
            bool usingGraph = shader != null && !ShaderUtil.ShaderHasError(shader) && shader.isSupported;
            if (!usingGraph)
            {
                if (shader != null) Debug.LogWarning($"[GatorSetup] SoftSurfaceGraph loaded but has errors/unsupported ({shader.name}); falling back to URP Lit.");
                shader = Shader.Find(UrpLitShaderName);
                if (shader == null) throw new Exception($"Neither SoftSurfaceGraph nor '{UrpLitShaderName}' shader could be found.");
            }

            Material mat = CreateOrUpdateMaterial(GatorMaterialPath, shader);
            if (usingGraph)
            {
                // SoftSurfaceGraph reference names: _MainTex, _Color, _Emission (same as built-in).
                SetTex(mat, "_MainTex", mainTex);
                SetColor(mat, "_Color", color);
                SetFloat(mat, "_Emission", emission);
            }
            else
            {
                // URP Lit approximation of "albedo*color (Lambert) + albedo*emission".
                SetTex(mat, "_BaseMap", mainTex);
                SetColor(mat, "_BaseColor", Color.Lerp(color, Color.white, 0.25f));
                SetFloat(mat, "_Smoothness", 0.1f);
                SetFloat(mat, "_Metallic", 0f);
                if (mat.HasProperty("_EmissionMap") && mainTex != null)
                {
                    mat.SetTexture("_EmissionMap", mainTex);
                    mat.SetColor("_EmissionColor", Color.white * emission);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
            }
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            string msg = $"{GatorMaterialPath} using shader '{shader.name}'" + (usingGraph ? " (pack SoftSurfaceGraph)" : " (FALLBACK URP Lit)");
            Debug.Log("[GatorSetup] FixMaterial: " + msg);
            return msg;
        }

        // ====================================================================================
        // 2. Animator controller AC_Gator (exactly per contract)
        // ====================================================================================

        static readonly string[] OneShotTriggers = { "Jump", "Bounce", "Clicked", "Spin", "Fear" };
        const float OneShotEnterDuration = 0.1f;   // AnyState -> one-shot
        const float ReturnDuration = 0.15f;        // one-shot / idle variant -> Locomotion
        const float ExitTime = 0.95f;

        static string BuildAnimatorInternal()
        {
            EnsureFolder(AnimationDir);
            Dictionary<string, AnimationClip> clips = LoadClips();

            // Reuse the existing controller asset (keeps its GUID so the prefab keeps its reference),
            // wiping all of its sub-assets; otherwise create a fresh one.
            AnimatorController ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null)
            {
                if (File.Exists(ControllerPath)) AssetDatabase.DeleteAsset(ControllerPath);
                ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath); // has "Base Layer"
            }
            else
            {
                ctrl.layers = Array.Empty<AnimatorControllerLayer>();
                ctrl.parameters = Array.Empty<AnimatorControllerParameter>();
                foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
                    if (sub != null && sub != ctrl && !(sub is AnimatorController))
                        Object.DestroyImmediate(sub, true);
                ctrl.AddLayer("Base Layer");
            }

            // ---- Parameters ----
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("IdleIndex", AnimatorControllerParameterType.Int);
            foreach (string t in OneShotTriggers) ctrl.AddParameter(t, AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Sit", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Lay", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Roll", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Death", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Revive", AnimatorControllerParameterType.Trigger);   // stand up from Sit / Lay / Death

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
            sm.entryPosition = new Vector3(0, 0, 0);
            sm.anyStatePosition = new Vector3(0, 300, 0);
            sm.exitPosition = new Vector3(900, 0, 0);

            // ---- Locomotion: 1D blend tree on Speed (Idle_A 0 / Walk 1 / Run 2) ----
            AnimatorState locomotion = ctrl.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            tree.name = "Locomotion";
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(Clip(clips, "Idle_A"), 0f);
            tree.AddChild(Clip(clips, "Walk"), 1f);
            tree.AddChild(Clip(clips, "Run"), 2f);
            sm.defaultState = locomotion;
            MoveState(sm, locomotion, new Vector3(300, 0, 0));

            // ---- Idle variants: Locomotion -> Idle_B/Idle_C when IdleIndex==1/2 && Speed<0.1 ----
            string[] idleVariants = { "Idle_B", "Idle_C" };
            for (int i = 0; i < idleVariants.Length; i++)
            {
                AnimatorState idle = sm.AddState(idleVariants[i], new Vector3(300 + 250 * i, -150, 0));
                idle.motion = Clip(clips, idleVariants[i]);

                AnimatorStateTransition enter = locomotion.AddTransition(idle);
                enter.hasExitTime = false;
                enter.exitTime = 0f;
                enter.hasFixedDuration = true;
                enter.duration = ReturnDuration;
                enter.AddCondition(AnimatorConditionMode.Equals, i + 1, "IdleIndex");
                enter.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");

                ReturnToLocomotion(idle, locomotion); // exit time 0.95, no condition
            }

            // ---- One-shots from AnyState on their trigger, back to Locomotion at exit time 0.95 ----
            for (int i = 0; i < OneShotTriggers.Length; i++)
            {
                string name = OneShotTriggers[i];
                AnimatorState s = sm.AddState(name, new Vector3(300 + 220 * i, 300, 0));
                s.motion = Clip(clips, name);
                AnyStateTrigger(sm, s, name);
                ReturnToLocomotion(s, locomotion);
            }

            // ---- Roll: one-shot like Jump (AnyState on "Roll", back to Locomotion at exit time 0.95) ----
            AnimatorState roll = sm.AddState("Roll", new Vector3(300 + 220 * OneShotTriggers.Length, 300, 0));
            roll.motion = Clip(clips, "Roll");
            AnyStateTrigger(sm, roll, "Roll");
            ReturnToLocomotion(roll, locomotion);

            // ---- Sit / Lay: loop until Speed > 0.1 or "Revive". Every other trigger (Jump, Bounce, Clicked, Spin,
            //      Fear, Sit/Lay, Roll, Death) leaves them through its AnyState transition (AnyState applies to all
            //      states; canTransitionToSelf = false only blocks re-entering the same state). ----
            string[] poses = { "Sit", "Lay" };
            for (int i = 0; i < poses.Length; i++)
            {
                AnimatorState pose = sm.AddState(poses[i], new Vector3(300 + 250 * i, 450, 0));
                pose.motion = HoldPoseClip(clips, poses[i]);   // non-looping copy: a pose, not a 0.42 s bounce
                AnyStateTrigger(sm, pose, poses[i]);
                AnimatorStateTransition walkOff = pose.AddTransition(locomotion);
                walkOff.hasExitTime = false;
                walkOff.exitTime = 0f;
                walkOff.hasFixedDuration = true;
                walkOff.duration = ReturnDuration;
                walkOff.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
                AnimatorStateTransition standUp = pose.AddTransition(locomotion);
                standUp.hasExitTime = false;
                standUp.exitTime = 0f;
                standUp.hasFixedDuration = true;
                standUp.duration = ReturnDuration;
                standUp.AddCondition(AnimatorConditionMode.If, 0f, "Revive");
            }

            // ---- Death: AnyState on "Death" (0.1 s); the clip doesn't loop and holds its last frame (on its back).
            //      No exit-time return: only "Revive" leaves it (0.25 s). ----
            AnimatorState death = sm.AddState("Death", new Vector3(800, 450, 0));
            death.motion = HoldPoseClip(clips, "Death");   // belt and braces: holds the flopped-over last frame
            AnyStateTrigger(sm, death, "Death");
            AnimatorStateTransition revive = death.AddTransition(locomotion);
            revive.hasExitTime = false;
            revive.exitTime = 0f;
            revive.hasFixedDuration = true;
            revive.duration = 0.25f;
            revive.AddCondition(AnimatorConditionMode.If, 0f, "Revive");

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            string msg = $"{ControllerPath} ({clips.Count} clips found, {sm.states.Length} states)";
            Debug.Log("[GatorSetup] BuildAnimator: " + msg);
            return msg;
        }

        static Dictionary<string, AnimationClip> LoadClips()
        {
            var clips = new Dictionary<string, AnimationClip>();
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(SrcAnimFbxPath))
            {
                if (o is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                    clips[c.name] = c;
            }
            if (clips.Count == 0) throw new Exception("No AnimationClips found in " + SrcAnimFbxPath);
            return clips;
        }

        static AnimationClip Clip(Dictionary<string, AnimationClip> clips, string name)
        {
            if (clips.TryGetValue(name, out AnimationClip c)) return c;
            Debug.LogWarning($"[GatorSetup] Clip '{name}' not found in {SrcAnimFbxPath}; state will have no motion.");
            return null;
        }

        /// The pack imports every clip with Loop Time on, so Sit / Lay / Death would replay their 0.42 s motion
        /// forever (a visible bounce). We don't edit the pack's import settings; instead keep a non-looping copy
        /// of the clip in Animation/<name>_Hold.anim (regenerated on every build, GUID kept) that settles on its
        /// last frame = the held pose.
        static AnimationClip HoldPoseClip(Dictionary<string, AnimationClip> clips, string name)
        {
            AnimationClip src = Clip(clips, name);
            if (src == null) return null;
            string path = AnimationDir + "/" + name + "_Hold.anim";
            AnimationClip copy = Object.Instantiate(src);
            copy.name = name + "_Hold";
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(copy);
            settings.loopTime = false;
            settings.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(copy, existing);   // keep the GUID so the controller's reference survives
                Object.DestroyImmediate(copy);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        static void AnyStateTrigger(AnimatorStateMachine sm, AnimatorState target, string trigger)
        {
            AnimatorStateTransition t = sm.AddAnyStateTransition(target);
            t.canTransitionToSelf = false;
            t.hasExitTime = false;
            t.exitTime = 0f;
            t.hasFixedDuration = true;
            t.duration = OneShotEnterDuration;
            t.AddCondition(AnimatorConditionMode.If, 0f, trigger);
        }

        static void ReturnToLocomotion(AnimatorState from, AnimatorState locomotion)
        {
            AnimatorStateTransition t = from.AddTransition(locomotion);
            t.hasExitTime = true;
            t.exitTime = ExitTime;
            t.hasFixedDuration = true;
            t.duration = ReturnDuration;
            t.offset = 0f;
        }

        static void MoveState(AnimatorStateMachine sm, AnimatorState state, Vector3 pos)
        {
            ChildAnimatorState[] states = sm.states;
            for (int i = 0; i < states.Length; i++)
                if (states[i].state == state) states[i].position = pos;
            sm.states = states;
        }

        // ====================================================================================
        // 3. Prefab GatorAssistant (+ Mocks/intro_flow.json)
        // ====================================================================================

        static string BuildPrefabInternal()
        {
            EnsureFolder(PrefabsDir);
            EnsureFolder(MocksDir);

            // Mocks first so MockEventSource can be wired to the TextAsset.
            TextAsset introFlow = WriteIntroFlow();

            GameObject srcPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SrcPrefabPath);
            if (srcPrefab == null) throw new FileNotFoundException("Source prefab not found", SrcPrefabPath);
            Material gatorMat = AssetDatabase.LoadAssetAtPath<Material>(GatorMaterialPath);
            if (gatorMat == null) { FixMaterialInternal(); gatorMat = AssetDatabase.LoadAssetAtPath<Material>(GatorMaterialPath); }
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null) { BuildAnimatorInternal(); controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath); }

            // Build in an isolated preview scene so the user's open scene is not touched.
            Scene preview = EditorSceneManager.NewPreviewScene();
            string msg;
            try
            {
                var root = new GameObject("GatorAssistant");
                SceneManager.MoveGameObjectToScene(root, preview);

                // ---- Model: nested instance of the untouched pack prefab (keeps LODGroup + LODs) ----
                var model = (GameObject)PrefabUtility.InstantiatePrefab(srcPrefab, preview);
                model.name = "Model";
                model.transform.SetParent(root.transform, false);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one;

                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] mats = r.sharedMaterials;
                    if (mats.Length == 0) mats = new Material[1];
                    for (int i = 0; i < mats.Length; i++) mats[i] = gatorMat;
                    r.sharedMaterials = mats;
                }

                // Animator lives on the model root ("GatorAssistant/Model"); Generic rig, no Avatar.
                Animator animator = model.GetComponent<Animator>();
                if (animator == null) animator = model.GetComponentInChildren<Animator>(true);
                if (animator == null) throw new Exception("No Animator found on " + SrcPrefabPath);
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

                // The pack prefab carries its own CapsuleCollider; disable it (the root gets a fitted one).
                foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) c.enabled = false;

                // The pack's LOD thresholds (95/70/35/5 % of screen height) assume a 1.6 m gator. A 12 cm desk pet
                // covers ~2 % of the screen from 3 m away, which culls every LOD -> invisible. Use desk-pet thresholds
                // and effectively never cull (LOD3 is the far/tiny fallback).
                LODGroup lodGroup = model.GetComponentInChildren<LODGroup>(true);
                if (lodGroup != null)
                {
                    LOD[] lods = lodGroup.GetLODs();
                    float[] heights = { 0.08f, 0.04f, 0.015f, 0.001f };
                    for (int i = 0; i < lods.Length; i++)
                        lods[i].screenRelativeTransitionHeight = i < heights.Length ? heights[i] : 0.001f;
                    lodGroup.SetLODs(lods);
                    lodGroup.RecalculateBounds();
                }

                // ---- Measure, scale to TargetHeight, put feet on y=0, centre on x/z ----
                if (!TryGetRendererBounds(model, out Bounds nb) || nb.size.y < 0.01f)
                {
                    Debug.LogWarning("[GatorSetup] Could not measure renderer bounds; using known native size " + FallbackNativeSize);
                    nb = new Bounds(FallbackNativeCenter, FallbackNativeSize);
                }
                float scale = TargetHeight / nb.size.y;
                model.transform.localScale = Vector3.one * scale;
                model.transform.localPosition = new Vector3(-nb.center.x * scale, -nb.min.y * scale, -nb.center.z * scale);

                Vector3 size = nb.size * scale;            // final (width, height, length) in metres
                float height = size.y, length = size.z, width = size.x;

                // ---- Root components ----
                var agent = root.AddComponent<NavMeshAgent>();
                agent.agentTypeID = 0;          // GatorController overrides with GatorNavSurface.GatorAgentTypeId
                agent.radius = AgentRadius;
                agent.height = AgentHeight;
                agent.enabled = false;          // GatorController enables it in Start, once the runtime NavMesh exists
                agent.baseOffset = 0f;
                agent.speed = 0.6f;
                agent.angularSpeed = 240f;
                agent.acceleration = 6f;
                agent.stoppingDistance = 0.1f;
                agent.autoBraking = true;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;

                var capsule = root.AddComponent<CapsuleCollider>();
                capsule.isTrigger = false;
                capsule.direction = 2; // Z (gator is long along its forward axis)
                capsule.radius = Mathf.Min(width, height) * 0.5f;
                capsule.height = Mathf.Max(length, capsule.radius * 2f);
                capsule.center = new Vector3(0f, height * 0.5f, 0f);

                var audio = root.AddComponent<AudioSource>();
                audio.spatialBlend = 1f;
                audio.playOnAwake = false;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 0.5f;
                audio.maxDistance = 8f;

                var added = new List<string>();
                foreach (string typeName in PrefabRuntimeComponents)
                    if (AddComponentByName(root, typeName) != null) added.Add(typeName.Substring(typeName.LastIndexOf('.') + 1));

                // Best-effort wiring of any null single-object fields (Animator, NavMeshAgent, sibling
                // components, intro_flow TextAsset). Runtime scripts should still GetComponent defensively.
                foreach (MonoBehaviour mb in root.GetComponents<MonoBehaviour>())
                    AutoWire(mb, new[] { root }, introFlow);

                // Size-dependent tunables: locomotion speeds scale with the model so the stride still matches
                // (no foot-sliding), and the caption floats just above the head.
                float speedScale = scale / ReferenceScale;
                foreach (MonoBehaviour mb in root.GetComponents<MonoBehaviour>())
                {
                    string tn = mb.GetType().FullName;
                    if (tn == ControllerType)
                    {
                        var so = new SerializedObject(mb);
                        SerializedProperty w = so.FindProperty("walkSpeed"), r = so.FindProperty("runSpeed");
                        if (w != null) w.floatValue = 0.6f * speedScale;
                        if (r != null) r.floatValue = 1.8f * speedScale;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    else if (tn == "Forge.Pet.GatorCaption")
                    {
                        var so = new SerializedObject(mb);
                        SerializedProperty off = so.FindProperty("offset");
                        if (off != null) off.vector3Value = new Vector3(0f, height + 0.08f, 0f);
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
                if (!success || saved == null) throw new Exception("PrefabUtility.SaveAsPrefabAsset failed for " + PrefabPath);

                msg = $"{PrefabPath} (native {nb.size.x:F3}×{nb.size.y:F3}×{nb.size.z:F3} m -> scale {scale:F3} -> " +
                      $"{width:F3} w × {height:F3} h × {length:F3} l m; agent r {agent.radius:F3}; components: {string.Join(", ", added)})";
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            Debug.Log("[GatorSetup] BuildPrefab: " + msg);
            return msg;
        }

        static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                Bounds b = r.bounds;
                if (b.size == Vector3.zero) continue;
                if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
            }
            return any;
        }

        /// Scripted hub->client events for MockEventSource. Format:
        /// { "name", "loop", "events": [ { "delay_ms" (relative to previous event), "type", "payload" } ] }
        static TextAsset WriteIntroFlow()
        {
            const string json = @"{
  ""name"": ""intro_flow"",
  ""loop"": false,
  ""events"": [
    { ""delay_ms"": 600,  ""type"": ""agent.thinking"",       ""payload"": { ""pet_id"": ""bit"" } },
    { ""delay_ms"": 1500, ""type"": ""agent.partial_answer"", ""payload"": { ""pet_id"": ""bit"", ""text"": ""Hi! I'm Bit,"" } },
    { ""delay_ms"": 700,  ""type"": ""agent.partial_answer"", ""payload"": { ""pet_id"": ""bit"", ""text"": ""Hi! I'm Bit, your gator assistant."" } },
    { ""delay_ms"": 700,  ""type"": ""agent.partial_answer"", ""payload"": { ""pet_id"": ""bit"", ""text"": ""Hi! I'm Bit, your gator assistant. Hold Space and ask me anything about the project."" } },
    { ""delay_ms"": 600,  ""type"": ""agent.answer_done"",    ""payload"": { ""pet_id"": ""bit"", ""text"": ""Hi! I'm Bit, your gator assistant. Hold Space and ask me anything about the project."" } },
    { ""delay_ms"": 3000, ""type"": ""agent.task_started"",   ""payload"": { ""pet_id"": ""bit"", ""task_id"": ""intro-1"", ""title"": ""Tidy up the README"" } },
    { ""delay_ms"": 1500, ""type"": ""agent.task_progress"",  ""payload"": { ""pet_id"": ""bit"", ""task_id"": ""intro-1"", ""text"": ""Reading the repo..."", ""pct"": 20 } },
    { ""delay_ms"": 1500, ""type"": ""agent.task_progress"",  ""payload"": { ""pet_id"": ""bit"", ""task_id"": ""intro-1"", ""text"": ""Editing README.md..."", ""pct"": 60 } },
    { ""delay_ms"": 1500, ""type"": ""agent.task_progress"",  ""payload"": { ""pet_id"": ""bit"", ""task_id"": ""intro-1"", ""text"": ""Running checks..."", ""pct"": 90 } },
    { ""delay_ms"": 1200, ""type"": ""agent.task_complete"",  ""payload"": { ""pet_id"": ""bit"", ""task_id"": ""intro-1"", ""summary"": ""README tidied: fixed setup steps and added a quickstart."", ""branch"": ""bit/intro-readme"" } }
  ]
}
";
            File.WriteAllText(ToFullPath(IntroFlowPath), json.Replace("\r\n", "\n"));
            AssetDatabase.ImportAsset(IntroFlowPath, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<TextAsset>(IntroFlowPath);
        }

        // ====================================================================================
        // 4. Desktop sandbox scene PetSandbox.unity (no XR rig, runtime NavMesh via GatorSandboxNav)
        // ====================================================================================

        static string BuildSandboxSceneInternal()
        {
            EnsureFolder(ScenesDir);
            EnsureFolder(MaterialsDir);
            DeleteLegacyNavMeshAsset();

            GameObject gatorPrefab = LoadOrBuildGatorPrefab();
            ConfirmSaveOpenScenes();

            // Default objects = Main Camera + Directional Light.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            Shader lit = Shader.Find(UrpLitShaderName);
            if (lit == null) throw new Exception($"Shader '{UrpLitShaderName}' not found (is URP active?).");
            Material floorMat = CreateOrUpdateMaterial(FloorMaterialPath, lit);
            SetColor(floorMat, "_BaseColor", new Color(0.55f, 0.55f, 0.55f, 1f));
            SetFloat(floorMat, "_Smoothness", 0.2f);
            Material propMat = CreateOrUpdateMaterial(PropMaterialPath, lit);
            SetColor(propMat, "_BaseColor", new Color(0.35f, 0.45f, 0.6f, 1f));
            SetFloat(propMat, "_Smoothness", 0.3f);
            Material deskMat = CreateOrUpdateMaterial(DeskMaterialPath, lit);
            SetColor(deskMat, "_BaseColor", new Color(0.55f, 0.4f, 0.25f, 1f));
            SetFloat(deskMat, "_Smoothness", 0.25f);
            EditorUtility.SetDirty(floorMat); EditorUtility.SetDirty(propMat); EditorUtility.SetDirty(deskMat);

            // ---- Environment ----
            var env = new GameObject("Environment");
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10×10 m at scale 1
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(FloorScale, 1f, FloorScale); // 20×20 m
            floor.transform.SetParent(env.transform, false);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // Obstacles: 0.3–0.8 m, kept >= 1.2 m apart and away from the spawn (origin), the desk
            // approach and the desktop camera rig at (0,0,-2). They become holes in the floor surface.
            var obstacles = new List<Collider>
            {
                AddProp(env, PrimitiveType.Cube, "Obstacle_Box_A", new Vector3(-2.5f, 0f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), propMat).GetComponent<Collider>(),
                AddProp(env, PrimitiveType.Cylinder, "Obstacle_Cyl_A", new Vector3(2.5f, 0f, -0.5f), new Vector3(0.4f, 0.8f, 0.4f), propMat).GetComponent<Collider>(),
                AddProp(env, PrimitiveType.Cube, "Obstacle_Box_B", new Vector3(-3.0f, 0f, 3.0f), new Vector3(0.8f, 0.8f, 0.8f), propMat).GetComponent<Collider>(),
                AddProp(env, PrimitiveType.Cylinder, "Obstacle_Cyl_B", new Vector3(3.0f, 0f, 3.0f), new Vector3(0.6f, 0.6f, 0.6f), propMat).GetComponent<Collider>(),
                AddProp(env, PrimitiveType.Cube, "Obstacle_Box_C", new Vector3(2.0f, 0f, -3.2f), new Vector3(0.3f, 0.3f, 0.3f), propMat).GetComponent<Collider>(),
                AddProp(env, PrimitiveType.Cylinder, "Obstacle_Cyl_C", new Vector3(-2.0f, 0f, -3.2f), new Vector3(0.5f, 0.7f, 0.5f), propMat).GetComponent<Collider>(),
            };
            // Desk 1.2 × 0.75 × 0.75 m, top at y = 0.75. Kept out of `obstacles`: GatorSandboxNav adds it as a hole itself.
            GameObject desk = AddProp(env, PrimitiveType.Cube, "Desk", new Vector3(0f, 0f, 2f), new Vector3(1.2f, 0.75f, 0.75f), deskMat);

            // ---- Gator ----
            var gator = (GameObject)PrefabUtility.InstantiatePrefab(gatorPrefab, scene);
            gator.name = "GatorAssistant";
            gator.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // ---- Navigation: two GatorNavSurfaces built at runtime by GatorSandboxNav ----
            var navGo = new GameObject("Navigation");
            var floorNavGo = new GameObject("FloorNav");
            floorNavGo.transform.SetParent(navGo.transform, false);
            var deskNavGo = new GameObject("DeskNav");
            deskNavGo.transform.SetParent(navGo.transform, false);
            Component floorNav = AddComponentByName(floorNavGo, NavSurfaceType);
            Component deskNav = AddComponentByName(deskNavGo, NavSurfaceType);
            Component sandboxNav = AddComponentByName(navGo, SandboxNavType);
            if (sandboxNav != null)
            {
                var so = new SerializedObject(sandboxNav);
                SetRef(so, "floor", floorNav);
                SetRef(so, "desk", deskNav);
                SetRef(so, "controller", GetComponentByName(gator, ControllerType));
                SetRef(so, "deskTop", desk.transform);
                SerializedProperty center = FindProp(so, "floorCenter");
                if (center != null) center.vector3Value = floor.transform.position;
                SerializedProperty size = FindProp(so, "floorSize");
                if (size != null) size.vector2Value = new Vector2(FloorSize, FloorSize);
                SerializedProperty obs = FindProp(so, "obstacles");
                if (obs != null)
                {
                    obs.arraySize = obstacles.Count;
                    for (int i = 0; i < obstacles.Count; i++) obs.GetArrayElementAtIndex(i).objectReferenceValue = obstacles[i];
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // ---- Desktop rig: DesktopRig + child camera (reuse the default Main Camera) ----
            Camera cam = scene.GetRootGameObjects().Select(g => g.GetComponent<Camera>()).FirstOrDefault(c => c != null);
            GameObject desktopRig = CreateDesktopRig(cam);

            // ---- Hotkeys (desktop only: no XR rig, no SandboxRigSelector) ----
            var tools = new GameObject("SandboxTools");
            Component hotkeys = AddComponentByName(tools, "Forge.Pet.PetSandboxHotkeys");
            if (hotkeys != null) AutoWire(hotkeys, new[] { desktopRig, gator }, null);

            // ---- Save + build settings (first) ----
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new Exception("EditorSceneManager.SaveScene failed: " + ScenePath);
            int index = PlaceInBuildSettings(ScenePath, null);

            string msg = $"{ScenePath} (runtime NavMesh: GatorSandboxNav {(sandboxNav != null ? "wired" : "MISSING")}, " +
                         $"surfaces {(floorNav != null && deskNav != null ? "ok" : "MISSING")}; build index {index})";
            Debug.Log("[GatorSetup] BuildSandboxScene: " + msg);
            return msg;
        }

        static GameObject AddProp(GameObject parent, PrimitiveType type, string name, Vector3 groundPos, Vector3 size, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            // Unity's cylinder primitive is 2 units tall at scale 1, the cube 1 unit.
            Vector3 scale = type == PrimitiveType.Cylinder ? new Vector3(size.x, size.y * 0.5f, size.z) : size;
            go.transform.localScale = scale;
            go.transform.localPosition = groundPos + new Vector3(0f, size.y * 0.5f, 0f);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        /// DesktopRig (CharacterController via RequireComponent) at (0,0,-2) with a child camera at eye height.
        /// Reuses `cam` if given, otherwise creates one.
        static GameObject CreateDesktopRig(Camera cam)
        {
            var desktopRig = new GameObject("DesktopRig");
            desktopRig.transform.SetPositionAndRotation(new Vector3(0f, 0f, -2f), Quaternion.identity);
            if (cam == null) cam = new GameObject("Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            cam.gameObject.name = "Camera";
            cam.tag = "MainCamera";
            cam.transform.SetParent(desktopRig.transform, false);
            cam.transform.localPosition = new Vector3(0f, 1.6f, 0f);           // world (0, 1.6, -2)
            cam.transform.localRotation = Quaternion.Euler(30f, 0f, 0f);         // look down at the gator
            cam.nearClipPlane = 0.05f;
            AddComponentByName(desktopRig, "Forge.Pet.DesktopRig");
            var cc = desktopRig.GetComponent<CharacterController>();
            if (cc != null) { cc.height = 1.7f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.85f, 0f); }
            return desktopRig;
        }

        // ====================================================================================
        // 5. XR sandbox scene PetSandboxXR.unity (Meta XR Simulator): MainSceneBuilder recipe + gator
        // ====================================================================================

        static string BuildXRSandboxSceneInternal()
        {
            EnsureFolder(ScenesDir);
            GameObject gatorPrefab = LoadOrBuildGatorPrefab();
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CameraRigPath);
            if (rigPrefab == null) throw new FileNotFoundException("OVRCameraRig prefab not found (Meta XR Core SDK not imported yet?)", CameraRigPath);
            var mrukPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MrukPath);
            if (mrukPrefab == null) throw new FileNotFoundException("MRUK prefab not found (MR Utility Kit not imported yet?)", MrukPath);
            WarnIfMixedRealityFeaturesOff();
            ConfirmSaveOpenScenes();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- Light + ambient (same as MainSceneBuilder) ----
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
            RenderSettings.skybox = null;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.65f);

            // ---- Player rig with passthrough as the background ----
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            var ovrManager = rig.GetComponent<OVRManager>();
            if (ovrManager == null) throw new Exception("OVRCameraRig prefab has no OVRManager: " + CameraRigPath);
            ovrManager.isInsightPassthroughEnabled = true;
            PrefabUtility.RecordPrefabInstancePropertyModifications(ovrManager);
            if (rig.GetComponent<OVRPassthroughLayer>() == null) rig.AddComponent<OVRPassthroughLayer>();
            foreach (var xrCam in rig.GetComponentsInChildren<Camera>(true))
            {
                xrCam.clearFlags = CameraClearFlags.SolidColor;
                xrCam.backgroundColor = Color.clear;
                PrefabUtility.RecordPrefabInstancePropertyModifications(xrCam);
            }

            PrefabUtility.InstantiatePrefab(mrukPrefab, scene);

            // Debug: draw tables so it's obvious the desk was detected
            var tables = new GameObject("TableHighlight").AddComponent<EffectMesh>();
            tables.MeshMaterial = AssetDatabase.LoadAssetAtPath<Material>(TableMaterialPath);
            tables.Labels = MRUKAnchor.SceneLabels.TABLE;

            var locator = new GameObject("DeskLocator").AddComponent<DeskLocator>();
            locator.head = rig.transform.Find("TrackingSpace/CenterEyeAnchor");
            if (locator.head == null) Debug.LogWarning("[GatorSetup] TrackingSpace/CenterEyeAnchor not found on OVRCameraRig; DeskLocator falls back to Camera.main.");

            // ---- Gator ----
            var gator = (GameObject)PrefabUtility.InstantiatePrefab(gatorPrefab, scene);
            gator.name = "GatorAssistant";
            gator.transform.SetPositionAndRotation(new Vector3(0f, 0f, 1f), Quaternion.identity);
            PrefabUtility.RecordPrefabInstancePropertyModifications(gator.transform);

            // ---- DeskLocator.deskFound (UnityEvent<MRUKAnchor>) -> GatorMrukAdapter.OnDeskFound ----
            string wiring = WireDeskFound(locator, gator);

            // ---- Desktop fallback (no XR device): SandboxRigSelector swaps OVRCameraRig <-> DesktopRig ----
            // Invisible floor so the DesktopRig's CharacterController has ground (MRUK only adds table meshes here).
            var fallbackFloor = new GameObject("DesktopFallbackFloor");
            fallbackFloor.layer = 2; // Ignore Raycast: keeps it out of pointer/poke rays
            fallbackFloor.transform.position = new Vector3(0f, -0.05f, 0f);
            fallbackFloor.AddComponent<BoxCollider>().size = new Vector3(FloorSize, 0.1f, FloorSize);

            GameObject desktopRig = CreateDesktopRig(null);
            Camera deskCam = desktopRig.GetComponentInChildren<Camera>(true);
            deskCam.clearFlags = CameraClearFlags.SolidColor;
            deskCam.backgroundColor = new Color(0.2f, 0.22f, 0.25f, 1f);
            desktopRig.SetActive(false); // saved inactive so it never competes with the XR camera; the selector enables it without XR

            var tools = new GameObject("SandboxTools");
            Component selector = AddComponentByName(tools, "Forge.Pet.SandboxRigSelector");
            if (selector != null)
            {
                var so = new SerializedObject(selector);
                SetRef(so, "xrRig", rig);
                SetRef(so, "desktopRig", desktopRig);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Component hotkeys = AddComponentByName(tools, "Forge.Pet.PetSandboxHotkeys");
            if (hotkeys != null) AutoWire(hotkeys, new[] { gator }, null);

            // ---- Save + build settings (right after the desktop sandbox) ----
            if (!EditorSceneManager.SaveScene(scene, XRScenePath)) throw new Exception("EditorSceneManager.SaveScene failed: " + XRScenePath);
            int index = PlaceInBuildSettings(XRScenePath, ScenePath);

            string msg = $"{XRScenePath} (rig {CameraRigPath}; MRUK {MrukPath}; deskFound: {wiring}; build index {index})";
            Debug.Log("[GatorSetup] BuildXRSandboxScene: " + msg + ". Activate the Meta XR Simulator, pick a room with a table, press Play.");
            return msg;
        }

        /// Adds a persistent (dynamic-argument) listener DeskLocator.deskFound -> adapter.OnDeskFound(MRUKAnchor).
        /// The adapter is looked up by type name and the method bound via reflection so this file does not need
        /// GatorMrukAdapter at compile time; UnityEventTools only needs a delegate whose Target is a UnityEngine.Object.
        static string WireDeskFound(DeskLocator locator, GameObject gator)
        {
            Component adapter = GetComponentByName(gator, MrukAdapterType);
            if (adapter == null)
            {
                Debug.LogWarning($"[GatorSetup] {MrukAdapterType} not on the gator prefab; DeskLocator.deskFound is NOT wired.");
                return "NOT wired (no adapter)";
            }
            var action = (UnityAction<MRUKAnchor>)Delegate.CreateDelegate(
                typeof(UnityAction<MRUKAnchor>), adapter, "OnDeskFound", false, false);
            if (action == null)
            {
                Debug.LogWarning($"[GatorSetup] {MrukAdapterType} has no public 'void OnDeskFound(MRUKAnchor)'; DeskLocator.deskFound is NOT wired.");
                return "NOT wired (no OnDeskFound(MRUKAnchor))";
            }
            if (locator.deskFound == null) locator.deskFound = new UnityEvent<MRUKAnchor>();
            for (int i = locator.deskFound.GetPersistentEventCount() - 1; i >= 0; i--)
                if (locator.deskFound.GetPersistentTarget(i) == adapter) UnityEventTools.RemovePersistentListener(locator.deskFound, i);
            UnityEventTools.AddPersistentListener<MRUKAnchor>(locator.deskFound, action);
            EditorUtility.SetDirty(locator);
            return "DeskLocator.deskFound -> GatorAssistant/GatorMrukAdapter.OnDeskFound";
        }

        /// Read-only check of the Meta project config. MainSceneBuilder (Core) owns enabling these; Project
        /// Settings are Hudson's, so we only warn.
        static void WarnIfMixedRealityFeaturesOff()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            if (config == null) return;
            if (config.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None ||
                config.sceneSupport == OVRProjectConfig.FeatureSupport.None ||
                config.anchorSupport == OVRProjectConfig.AnchorSupport.Disabled)
                Debug.LogWarning("[GatorSetup] Passthrough / scene / anchor support is off in the Meta project config. " +
                                 "Ask Hudson to run Forge > Build Main Scene (it enables them); the XR sandbox needs them.");
        }

        // ====================================================================================
        // Scene helpers
        // ====================================================================================

        static GameObject LoadOrBuildGatorPrefab()
        {
            GameObject gatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (gatorPrefab == null) { BuildPrefabInternal(); gatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath); }
            if (gatorPrefab == null) throw new Exception("GatorAssistant prefab missing: " + PrefabPath);
            return gatorPrefab;
        }

        static void ConfirmSaveOpenScenes()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new OperationCanceledException("User cancelled saving the currently open scene(s).");
        }

        /// Puts `path` in Build Settings right after `after` (or last when `after` is null or not listed).
        /// Never takes slot 0: that belongs to Hudson's Main.unity for the Quest build. Returns the resulting index.
        static int PlaceInBuildSettings(string path, string after)
        {
            // Build Settings live in ProjectSettings/, which only Core (Hudson) edits. Sandbox scenes are
            // play-mode only, so this is opt-in; flip it when a sandbox must ship in a build.
            if (!AddScenesToBuildSettings) return -1;
            List<EditorBuildSettingsScene> list = EditorBuildSettings.scenes.Where(s => s.path != path).ToList();
            int index = list.Count;
            if (after != null)
            {
                int a = list.FindIndex(s => s.path == after);
                index = a >= 0 ? a + 1 : list.Count;
            }
            list.Insert(index, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
            return index;
        }

        static void DeleteLegacyNavMeshAsset()
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(LegacyNavMeshDataPath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyNavMeshDataPath);
                Debug.Log("[GatorSetup] Deleted legacy baked NavMesh " + LegacyNavMeshDataPath);
            }
            if (AssetDatabase.IsValidFolder(LegacyNavMeshDataDir) && AssetDatabase.FindAssets("", new[] { LegacyNavMeshDataDir }).Length == 0)
                AssetDatabase.DeleteAsset(LegacyNavMeshDataDir);
        }

        static SerializedProperty FindProp(SerializedObject so, string name)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p == null) Debug.LogWarning($"[GatorSetup] {so.targetObject.GetType().Name} has no serialized field '{name}'.");
            return p;
        }

        static void SetRef(SerializedObject so, string name, Object value)
        {
            SerializedProperty p = FindProp(so, name);
            if (p == null) return;
            if (p.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogWarning($"[GatorSetup] {so.targetObject.GetType().Name}.{name} is not an object reference.");
                return;
            }
            p.objectReferenceValue = value;
        }

        static Component GetComponentByName(GameObject go, string fullName)
        {
            Type t = FindComponentType(fullName);
            return t != null ? go.GetComponent(t) : null;
        }

        // ====================================================================================
        // Helpers
        // ====================================================================================

        static Type FindComponentType(string fullName)
        {
            foreach (Type t in TypeCache.GetTypesDerivedFrom<Component>())
                if (t.FullName == fullName && !t.IsAbstract) return t;
            return null;
        }

        /// AddComponent(typeof(X)) by full type name; warns and returns null if the type is missing.
        static Component AddComponentByName(GameObject go, string fullName)
        {
            Type t = FindComponentType(fullName);
            if (t == null)
            {
                Debug.LogWarning($"[GatorSetup] Type '{fullName}' not found (not compiled yet?); skipping on '{go.name}'.");
                return null;
            }
            try
            {
                Component existing = go.GetComponent(t);
                return existing != null ? existing : go.AddComponent(t);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GatorSetup] AddComponent({fullName}) on '{go.name}' failed: {e.Message}");
                return null;
            }
        }

        /// Fills null top-level object-reference fields of a component:
        ///  * TextAsset fields -> textAsset (intro_flow.json)
        ///  * GameObject/Transform fields -> candidate whose name matches the field name
        ///    (desktop -> DesktopRig, xr/vr/ovr -> XRRig/OVRCameraRig, gator/pet -> GatorAssistant)
        ///  * Component-typed fields -> first matching component under the candidates
        ///    (keyword-matched candidate searched first).
        static void AutoWire(Component c, IList<GameObject> candidates, TextAsset textAsset)
        {
            var so = new SerializedObject(c);
            SerializedProperty p = so.GetIterator();
            var wired = new List<string>();
            bool enterChildren = true;
            while (p.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (p.propertyType != SerializedPropertyType.ObjectReference || p.name == "m_Script") continue;
                if (p.objectReferenceValue != null) continue;

                string typeName = p.type.Replace("PPtr<", "").Replace(">", "").Replace("$", "");
                string field = p.name.ToLowerInvariant();
                Object value = null;

                if (typeName == "TextAsset") value = textAsset;
                else
                {
                    GameObject preferred = PickByKeyword(field, candidates);
                    if (typeName == "GameObject" || typeName == "Object") value = preferred;
                    else if (typeName == "Transform") value = preferred != null ? preferred.transform : null;
                    else
                    {
                        IEnumerable<GameObject> order = preferred != null
                            ? new[] { preferred }.Concat(candidates.Where(g => g != preferred))
                            : candidates;
                        foreach (GameObject g in order)
                        {
                            if (g == null) continue;
                            value = g.GetComponentsInChildren<Component>(true)
                                .FirstOrDefault(comp => comp != null && comp != c && comp.GetType().Name == typeName);
                            if (value != null) break;
                        }
                    }
                }
                if (value != null)
                {
                    p.objectReferenceValue = value;
                    wired.Add($"{p.name}={value.name}");
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            if (wired.Count > 0) Debug.Log($"[GatorSetup] Auto-wired {c.GetType().Name}: {string.Join(", ", wired)}");
        }

        static GameObject PickByKeyword(string field, IList<GameObject> candidates)
        {
            GameObject Find(string n) => candidates.FirstOrDefault(g => g != null && g.name == n);
            if (field.Contains("desktop")) return Find("DesktopRig");
            if (field.Contains("xr") || field.Contains("vr") || field.Contains("ovr") || field.Contains("quest")) return Find("XRRig") ?? Find("OVRCameraRig");
            if (field.Contains("gator") || field.Contains("pet")) return Find("GatorAssistant");
            return null;
        }

        static Material CreateOrUpdateMaterial(string path, Shader shader)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            return mat;
        }

        static void SetTex(Material m, string prop, Texture t)
        {
            if (m.HasProperty(prop)) m.SetTexture(prop, t);
            else Debug.LogWarning($"[GatorSetup] {m.shader.name} has no '{prop}'");
        }

        static void SetColor(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
            else Debug.LogWarning($"[GatorSetup] {m.shader.name} has no '{prop}'");
        }

        static void SetFloat(Material m, string prop, float f)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, f);
            else Debug.LogWarning($"[GatorSetup] {m.shader.name} has no '{prop}'");
        }

        /// Recursively creates "Assets/a/b/c" with AssetDatabase.CreateFolder.
        static void EnsureFolder(string path)
        {
            path = path.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static string ToFullPath(string assetPath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);
    }
}
