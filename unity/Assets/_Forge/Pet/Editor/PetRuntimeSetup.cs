// PetRuntimeSetup.cs — Forge/Pet/8. Build PetRuntime Prefab
//
// Assembles Assets/_Forge/Pet/Prefabs/PetRuntime.prefab, the ONE prefab Core's MainSceneBuilder drops into Main.unity
// (by that path: it is a contract). Everything Bit needs at the desk, nothing scene-specific:
//
//   PetRuntime
//   ├─ GatorAssistant      nested GatorAssistant.prefab instance + the in-scene additions PetMain.unity used to carry:
//   │   │                  WakeListener, GatorTalkAnimator, GatorSummon, GatorFollowUp
//   │   └─ Voice           AudioSource + StreamedPcmTextToSpeech, wired into GatorAgentBridge.tts
//   ├─ WhistleButton       desk puck (+ Interaction SDK poke / ray), the GatorMrukAdapter's whistleTemplate (one per table)
//   ├─ GatorCrew           lead = GatorAssistant, crewPrefab = GatorAssistant.prefab
//   ├─ GatorHatchery       the egg + picker (+ Interaction SDK poke / ray)
//   ├─ ArrivalGreeting     the first-arrival card + START ring (ArrivalSetup; inactive until used; ring = Core DockRing
//   │                      + Interaction SDK ray / poke canvas interaction)
//   └─ (root) ArrivalDirector   DeskPresence → hop / greeting / digest / START / code mode choreography, workspace.mode to the hub
//
// Built in a preview scene from the same helpers the "Add ... To Open Scene" menus use (WhistleSetup, CrewSetup) with Undo
// off. Idempotent: re-running overwrites the prefab (GUID kept). Also the last step of Forge/Pet/Build All.
// -executeMethod Forge.Pet.Editor.PetRuntimeSetup.BuildPetRuntimePrefab

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Forge.Pet.Editor
{
    public static class PetRuntimeSetup
    {
        /// Contract with Core: MainSceneBuilder instantiates this path.
        public const string PrefabPath = "Assets/_Forge/Pet/Prefabs/PetRuntime.prefab";
        /// Where Bit starts inside PetRuntime (the root sits at the room origin; the floor NavMesh is built around him at runtime).
        static readonly Vector3 GatorSpawn = new Vector3(1f, 0f, 0.5f);

        [MenuItem("Forge/Pet/8. Build PetRuntime Prefab", priority = 8)]
        public static void BuildPetRuntimePrefab()
        {
            try
            {
                string msg = Build();
                AssetDatabase.SaveAssets();
                Debug.Log("[PetRuntimeSetup] " + msg);
            }
            catch (Exception e)
            {
                Debug.LogError("[PetRuntimeSetup] FAILED: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
            }
        }

        /// Builds (or rebuilds) the prefab; returns a one-line summary. Throws on failure.
        public static string Build()
        {
            GameObject gatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GatorSetup.PrefabPath);
            if (gatorPrefab == null)
            {
                GatorSetup.BuildPrefab();
                gatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GatorSetup.PrefabPath);
            }
            if (gatorPrefab == null) throw new Exception("GatorAssistant prefab missing: " + GatorSetup.PrefabPath);

            Scene preview = EditorSceneManager.NewPreviewScene();
            bool undoWas = WhistleSetup.UseUndo;
            WhistleSetup.UseUndo = false;
            string msg;
            try
            {
                var root = new GameObject("PetRuntime");
                SceneManager.MoveGameObjectToScene(root, preview);

                // ---- Bit: nested prefab instance ----
                var gator = (GameObject)PrefabUtility.InstantiatePrefab(gatorPrefab, preview);
                gator.name = "GatorAssistant";
                gator.transform.SetParent(root.transform, false);
                gator.transform.localPosition = GatorSpawn;
                gator.transform.localRotation = Quaternion.identity;
                var controller = gator.GetComponent<GatorController>();
                if (controller == null) throw new Exception("GatorAssistant prefab has no GatorController.");
                var bridge = gator.GetComponent<GatorAgentBridge>();
                var adapter = gator.GetComponent<GatorMrukAdapter>();
                if (adapter == null) Debug.LogWarning("[PetRuntimeSetup] GatorAssistant has no GatorMrukAdapter; the desk NavMesh won't be built in Main.");

                // ---- Voice path (what PetMain.unity had on the instance) ----
                StreamedPcmTextToSpeech voice = WhistleSetup.AddVoice(gator);
                WhistleSetup.AddTalkAnimator(gator, voice);
                if (gator.GetComponent<WakeListener>() == null) gator.AddComponent<WakeListener>();

                // ---- Whistle ----
                WhistleButton button = WhistleSetup.CreateButton(controller, root.transform);
                string whistleIsdk = WhistleSetup.AddInteractionSdk(button, warnNoInteractors: false);
                WhistleSetup.AddSummon(gator, button, voice);
                WhistleSetup.AddFollowUp(gator, voice);
                if (adapter != null) WhistleSetup.SetRef(adapter, "whistleTemplate", button);

                // ---- Crew ----
                CrewSetup.CreateCrew(controller, gatorPrefab, root.transform);
                GatorHatchery egg = CrewSetup.CreateHatchery(controller, root.transform);
                string eggIsdk = CrewSetup.AddInteraction(egg);

                // ---- Arrival choreography: greeting card + START ring, and the director ----
                ArrivalGreeting greeting = ArrivalSetup.CreateGreeting(root.transform);
                string greetingIsdk = ArrivalSetup.AddCanvasInteraction(greeting);
                var director = root.AddComponent<ArrivalDirector>();
                WhistleSetup.SetRef(director, "controller", controller);
                if (bridge != null) WhistleSetup.SetRef(director, "bridge", bridge);
                if (adapter != null) WhistleSetup.SetRef(director, "adapter", adapter);
                WhistleSetup.SetRef(director, "greeting", greeting);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
                if (!success || saved == null) throw new Exception("PrefabUtility.SaveAsPrefabAsset failed for " + PrefabPath);

                int gatorComponents = gator.GetComponents<MonoBehaviour>().Length;
                msg = $"{PrefabPath} (GatorAssistant with {gatorComponents} scripts + Voice; WhistleButton ({whistleIsdk}); GatorCrew; GatorHatchery ({eggIsdk}); ArrivalGreeting ({greetingIsdk}); ArrivalDirector)";
            }
            finally
            {
                WhistleSetup.UseUndo = undoWas;
                EditorSceneManager.ClosePreviewScene(preview);
            }
            return msg;
        }
    }
}
