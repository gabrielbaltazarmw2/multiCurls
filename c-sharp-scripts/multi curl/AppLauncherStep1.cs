using UnityEngine;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;

public class AppLauncherStep1 : MonoBehaviour
{
    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public DracoMultiCurlStep1 DracoCurl;

    // =========================================================
    // Process state (single process for STEP 1)
    // =========================================================
    private Process process = null;

    // Keep for compatibility (not used)
    private StreamWriter messageStream;

    // =========================================================
    // Completion queue (main-thread safe)
    // =========================================================
    private bool _hasPendingCompletion = false;
    private int _pendingBatchStart = 0;
    private int _pendingBatchCount = 0;
    private int _pendingExitCode = 0;
    private string _pendingReason = "";

    /// <summary>
    /// STEP 1: Start a batch-aware process. Still enforces one process at a time.
    /// When it finishes, we notify DracoCurl with (batchStart, batchCount, exitCode).
    /// </summary>
    public async void StartBatch(string appName, string appArgs, int batchStart, int batchCount)
    {
        // Wait while a previous curl process is still running
        while (process != null && !process.HasExited)
            await Task.Delay(1);

        try
        {
            process = new Process();

            process.StartInfo.FileName = Application.persistentDataPath + "/Executables/" + appName;
            process.StartInfo.Arguments = appArgs;

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            // IMPORTANT: for batch-aware completion, use Exited
            process.EnableRaisingEvents = true;

            // Capture batch metadata in closure
            process.Exited += (sender, e) =>
            {
                int exitCode = -1;
                string reason = "";

                try { exitCode = process.ExitCode; }
                catch (Exception ex) { reason = ex.Message; }

                // Store completion to be processed in Update() (Unity main thread)
                _pendingBatchStart = batchStart;
                _pendingBatchCount = batchCount;
                _pendingExitCode = exitCode;
                _pendingReason = reason;
                _hasPendingCompletion = true;
            };

            // Optional: keep reading output (not used for logic in STEP 1)
            process.OutputDataReceived += (sender, e) => { /* keep silent */ };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    UnityEngine.Debug.LogError(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Keep stdin handle (not used, but preserved)
            messageStream = process.StandardInput;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("Unable to launch app: " + ex.Message);
        }
    }

    private void Update()
    {
        // Deliver completion on the Unity main thread (safe with DracoCurl.Update)
        if (_hasPendingCompletion)
        {
            _hasPendingCompletion = false;

            if (DracoCurl != null)
                DracoCurl.AdvanceBatch(_pendingBatchStart, _pendingBatchCount, _pendingExitCode, _pendingReason);
        }
    }

    public void KillProcess()
    {
        if (process != null && !process.HasExited)
            process.Kill();
    }

    private void OnDestroy()
    {
        KillProcess();
    }
}
