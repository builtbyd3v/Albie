// Editor only: pressing Play starts the hub (hub/.venv uvicorn main:app) when nothing answers on localhost:8000, so
// Main never opens to "Hub offline." because someone forgot a terminal. The hub keeps running between Play sessions
// (it takes a few seconds to boot) and is stopped when the editor quits, if this script started it.
//
//   Forge > Hub > Auto-start on Play   (on by default, per machine)
//   Forge > Hub > Start / Stop / Restart / Open Log
//
// It only ever starts a local hub: if PlayerPrefs Forge.HubUrl points at another machine, Play leaves it alone.
// A hub that answers but is older code (its /health has no "slack" / "screen") is reported, not killed: use Restart.
// Output goes to hub/.data/hub.log. Needs hub/.venv (see hub/README.md); without it, a warning says what to run.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Forge.Pet.Editor
{
    [InitializeOnLoad]
    public static class HubAutoStart
    {
        const string AutoPref = "Forge.HubAutoStart";
        const string PidKey = "Forge.HubAutoStart.Pid";   // SessionState: survives script reloads, not editor restarts
        const string Menu = "Forge/Hub/";
        const int Port = 8000;
        const float BootTimeout = 25f;

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };

        static HubAutoStart()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.quitting += OnQuit;
        }

        static bool AutoStart
        {
            get => EditorPrefs.GetBool(AutoPref, true);
            set => EditorPrefs.SetBool(AutoPref, value);
        }

        static string RepoDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string HubDir => Path.Combine(RepoDir, "hub");
        static string LogPath => Path.Combine(HubDir, ".data", "hub.log");
        static bool Windows => Application.platform == RuntimePlatform.WindowsEditor;
        static string Uvicorn => Windows ? Path.Combine(HubDir, ".venv", "Scripts", "uvicorn.exe")
                                         : Path.Combine(HubDir, ".venv", "bin", "uvicorn");

        // ---------------------------------------------------------------- Play

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode || !AutoStart) return;
            if (!PointsAtThisMachine()) return;

            string health = Health();
            if (health != null)
            {
                if (!health.Contains("\"slack\"") || !health.Contains("\"screen\""))
                    Debug.LogWarning($"[Hub] Something older is answering on :{Port} (its /health has no slack/screen, so no Slack " +
                                     "routes or screen bridge). Forge > Hub > Restart replaces it with this checkout's hub.");
                return;
            }
            if (Start()) WaitUntilUp();
        }

        static bool PointsAtThisMachine()
        {
            string url = PlayerPrefs.GetString(ForgeNet.HubUrlPref, "");
            if (string.IsNullOrEmpty(url)) return true;   // ForgeNet's default, http://localhost:8000
            return url.Contains("localhost") || url.Contains("127.0.0.1");
        }

        // ---------------------------------------------------------------- start / stop

        static bool Start()
        {
            if (!File.Exists(Uvicorn))
            {
                Debug.LogWarning($"[Hub] Not started: {Uvicorn} is missing. Set the hub up once (hub/README.md): " +
                                 "cd hub && python3 -m venv .venv && .venv/bin/pip install -r requirements.txt");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} started from Unity (Play) =====\n");

            // Through the shell so the hub writes straight to the log file: Unity never holds its pipes, and `exec`
            // makes the pid we keep uvicorn's own (SIGINT then runs the hub's shutdown, which stops the screen bridge).
            string args = $"main:app --host 0.0.0.0 --port {Port}";
            var psi = Windows
                ? new ProcessStartInfo("cmd.exe", $"/c \"\"{Uvicorn}\" {args} >> \"{LogPath}\" 2>&1\"")
                : new ProcessStartInfo("/bin/sh", $"-c \"exec '{Uvicorn}' {args} >> '{LogPath}' 2>&1\"");
            psi.WorkingDirectory = HubDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            try
            {
                Process p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned null");
                SessionState.SetInt(PidKey, p.Id);
                Debug.Log($"[Hub] Starting the hub (pid {p.Id}) from {HubDir}; log: {LogPath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Hub] Could not start the hub: {e.Message}");
                return false;
            }
        }

        // Block entering Play until /health answers (or BootTimeout / Cancel), so the arrival digest finds the hub.
        static void WaitUntilUp()
        {
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed.TotalSeconds < BootTimeout)
                {
                    float t = (float)clock.Elapsed.TotalSeconds;
                    if (EditorUtility.DisplayCancelableProgressBar("Starting the hub",
                            $"uvicorn main:app on :{Port} ({t:0}s; Slack, CAD worker, screen bridge)", t / BootTimeout))
                    {
                        Debug.Log("[Hub] Not waiting any longer; Unity connects by itself when the hub is up.");
                        return;
                    }
                    if (Health() != null)
                    {
                        Debug.Log($"[Hub] Up after {t:0.0}s.");
                        return;
                    }
                    if (!OurProcessAlive())
                    {
                        Debug.LogError($"[Hub] The hub exited while starting. See {LogPath}");
                        return;
                    }
                    Thread.Sleep(400);
                }
                Debug.LogWarning($"[Hub] Not up after {BootTimeout:0}s; Unity keeps retrying. See {LogPath}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static bool OurProcessAlive()
        {
            int pid = SessionState.GetInt(PidKey, 0);
            if (pid == 0) return false;
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        static void Stop(int pid)
        {
            if (pid == 0) return;
            try
            {
                if (Windows) Process.GetProcessById(pid).Kill();
                else Process.Start(new ProcessStartInfo("/bin/kill", $"-INT {pid}") { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(2000);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Hub] Could not stop pid {pid}: {e.Message}");
            }
        }

        // Whatever listens on the port (the hub from a terminal, a stale one): macOS / Linux only.
        static int ListenerPid()
        {
            if (Windows) return 0;
            try
            {
                var p = Process.Start(new ProcessStartInfo("/usr/sbin/lsof", $"-tiTCP:{Port} -sTCP:LISTEN")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                string output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(2000);
                string first = output.Split('\n')[0].Trim();
                return int.TryParse(first, out int pid) ? pid : 0;
            }
            catch { return 0; }
        }

        static void OnQuit()
        {
            if (OurProcessAlive()) Stop(SessionState.GetInt(PidKey, 0));
        }

        static string Health()
        {
            try
            {
                var response = Http.GetAsync($"http://127.0.0.1:{Port}/health").GetAwaiter().GetResult();
                return response.IsSuccessStatusCode ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- menu

        [MenuItem(Menu + "Auto-start on Play", priority = 1)]
        static void ToggleAuto() => AutoStart = !AutoStart;

        [MenuItem(Menu + "Auto-start on Play", true)]
        static bool ToggleAutoCheck()
        {
            UnityEditor.Menu.SetChecked(Menu + "Auto-start on Play", AutoStart);
            return true;
        }

        [MenuItem(Menu + "Start Hub", priority = 20)]
        static void MenuStart()
        {
            if (Health() != null) { Debug.Log($"[Hub] Already answering on :{Port}."); return; }
            if (Start()) WaitUntilUp();
        }

        [MenuItem(Menu + "Stop Hub", priority = 21)]
        static void MenuStop()
        {
            int pid = OurProcessAlive() ? SessionState.GetInt(PidKey, 0) : ListenerPid();
            if (pid == 0) { Debug.Log("[Hub] No hub running."); return; }
            Stop(pid);
            SessionState.EraseInt(PidKey);
            Debug.Log($"[Hub] Stopped pid {pid}.");
        }

        [MenuItem(Menu + "Restart Hub", priority = 22)]
        static void MenuRestart()
        {
            MenuStop();
            var clock = Stopwatch.StartNew();
            while (Health() != null && clock.Elapsed.TotalSeconds < 8) Thread.Sleep(300);
            if (Start()) WaitUntilUp();
        }

        [MenuItem(Menu + "Open Log", priority = 40)]
        static void OpenLog()
        {
            if (File.Exists(LogPath)) EditorUtility.OpenWithDefaultApp(LogPath);
            else Debug.Log($"[Hub] No log yet ({LogPath}).");
        }
    }
}
