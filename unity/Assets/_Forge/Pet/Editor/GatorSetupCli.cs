// GatorSetupCli.cs — batch-mode entry point for the reproducible gator pet setup.
//
//   Unity -batchmode -projectPath <repo>/unity \
//         -executeMethod Forge.Pet.Editor.GatorSetupCli.BuildAllAndQuit -logFile -
//
// Exits 0 when every step of GatorSetup.BuildAll succeeded, 1 otherwise.

using System;
using UnityEditor;
using UnityEngine;

namespace Forge.Pet.Editor
{
    public static class GatorSetupCli
    {
        public static void BuildAllAndQuit()
        {
            int exitCode = 0;
            try
            {
                GatorSetup.BuildAll();
                AssetDatabase.SaveAssets();
                if (!GatorSetup.LastBuildSucceeded)
                {
                    Debug.LogError("[GatorSetupCli] BuildAll reported failures (see errors above).");
                    exitCode = 1;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                exitCode = 1;
            }
            Debug.Log($"[GatorSetupCli] Exiting with code {exitCode}.");
            EditorApplication.Exit(exitCode);
        }
    }
}
