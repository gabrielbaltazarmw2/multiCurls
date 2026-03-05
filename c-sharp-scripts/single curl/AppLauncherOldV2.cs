using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Launches an external executable (curl.exe) and notifies DracoCurl when the batch finishes.
/// This is the single-process (pre-multi-curl) version: it runs one process at a time.
/// </summary>
public class AppLauncherOldV2 : MonoBehaviour
{
    [Header("References")]
    public DracoCurlOldV2 dracoCurl;

    [Header("Executable")]
    [Tooltip("Folder inside persistentDataPath that contains the executable (e.g., 'Executables').")]
    public string executablesFolder = "Executables";

    private Process _process;

    /// <summary>
    /// Starts the external process. If a previous process is still running, it waits for it to finish.
    /// </summary>
    public async void StartProcess(string appName, string appArgs)
    {
        if (dracoCurl == null)
        {
            Debug.LogError("[AppLauncher] Missing reference: dracoCurl.");
            return;
        }

        // Wait until any previous process is done (pre-multi-curl behavior)
        while (_process != null && !_process.HasExited)
            await Task.Delay(1);

        try
        {
            string exePath = Path.Combine(Application.persistentDataPath, executablesFolder, appName);

            if (!File.Exists(exePath))
            {
                Debug.LogError($"[AppLauncher] Executable not found: {exePath}");
                return;
            }

            _process = new Process();
            _process.StartInfo.FileName = exePath;
            _process.StartInfo.Arguments = appArgs;

            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.CreateNoWindow = true;

            // Use Exited to signal "batch completed"
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;

            // Optional: log output for debugging
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;

            bool started = _process.Start();
            if (!started)
            {
                Debug.LogError("[AppLauncher] Failed to start process.");
                return;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Debug.Log($"[AppLauncher] Started: {exePath} {appArgs}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AppLauncher] Unable to launch app: {e.Message}");
        }
    }

    /// <summary>
    /// Called when the external process exits.
    /// IMPORTANT: This callback is not guaranteed to run on Unity main thread.
    /// So we only signal and do minimal work here.
    /// </summary>
    private void OnProcessExited(object sender, EventArgs e)
    {
        // Pre-multi-curl behavior: when process finishes, advance the batch once.
        // Warning: this may run on a non-main thread; if AdvanceBatch touches Unity API,
        // you should marshal this call back to Update(). In your original version it was called
        // from a data received callback anyway, so behavior is equivalent.
        try
        {
            dracoCurl.AdvanceBatch();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AppLauncher] Error while advancing batch: {ex.Message}");
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        // Do NOT drive pipeline logic from stdout lines. Just log if needed.
        // if (!string.IsNullOrEmpty(e.Data))
        //     Debug.Log($"[AppLauncher][stdout] {e.Data}");
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            Debug.LogError($"[AppLauncher][stderr] {e.Data}");
    }

    public void KillProcess()
    {
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill();
        }
        catch (Exception e)
        {
            Debug.LogError($"[AppLauncher] Failed to kill process: {e.Message}");
        }
    }

    private void OnDestroy()
    {
        KillProcess();
    }
}