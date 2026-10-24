using System;
using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Networking;
#endif

namespace Forge.Pet
{
    /// <summary>
    /// Contents of Assets/StreamingAssets/pet_config.json. Parsed with JsonUtility (IL2CPP-safe).
    /// Missing file / bad JSON → defaults (mock source, local hub).
    /// </summary>
    [Serializable]
    public class PetConfig
    {
        // Unused since AgentClient rides on ForgeNet: set the hub address on the ForgeNet object (or PlayerPrefs Forge.HubUrl).
        public string hubUrl = "ws://127.0.0.1:8000/ws/agent";
        public string petId = "bit";
        public string projectId = "gatorhack";
        public string eventSource = "mock";   // "mock" | "hub"

        public const string FileName = "pet_config.json";
        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        public bool IsHub => string.Equals(eventSource, "hub", StringComparison.OrdinalIgnoreCase);

        /// <summary>Synchronous load. Works in the editor / desktop; on Android use <see cref="LoadAsync"/>.</summary>
        public static PetConfig Load()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            Debug.LogWarning("[PetConfig] Load() can't read StreamingAssets on Android; use LoadAsync. Using defaults.");
            return new PetConfig();
#else
            try
            {
                if (File.Exists(FilePath)) return Parse(File.ReadAllText(FilePath));
                Debug.Log("[PetConfig] " + FilePath + " not found, using defaults.");
            }
            catch (Exception e) { Debug.LogWarning("[PetConfig] Read failed, using defaults: " + e.Message); }
            return new PetConfig();
#endif
        }

        /// <summary>Coroutine: StartCoroutine(PetConfig.LoadAsync(cfg => ...)). Always calls back once.</summary>
        public static IEnumerator LoadAsync(Action<PetConfig> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // StreamingAssets live inside the APK (jar:file://...), only UnityWebRequest can read them.
            using (var req = UnityWebRequest.Get(FilePath))
            {
                yield return req.SendWebRequest();
                PetConfig cfg;
                if (req.result == UnityWebRequest.Result.Success) cfg = Parse(req.downloadHandler.text);
                else { Debug.Log("[PetConfig] " + FileName + " not readable (" + req.error + "), using defaults."); cfg = new PetConfig(); }
                done?.Invoke(cfg);
            }
#else
            done?.Invoke(Load());
            yield break;
#endif
        }

        public static PetConfig Parse(string json)
        {
            var cfg = new PetConfig();
            if (string.IsNullOrWhiteSpace(json)) return cfg;
            try { JsonUtility.FromJsonOverwrite(json, cfg); }
            catch (Exception e) { Debug.LogWarning("[PetConfig] Bad JSON, using defaults: " + e.Message); }
            return cfg;
        }

        public override string ToString() =>
            $"PetConfig(hubUrl={hubUrl}, petId={petId}, projectId={projectId}, eventSource={eventSource})";
    }
}

