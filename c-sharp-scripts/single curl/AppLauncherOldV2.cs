using UnityEngine;             // Permite usar MonoBehaviour, Application.persistentDataPath, Debug.Log, etc.
using System;                  // Tipos base do .NET, Exception, EventArgs, etc.
using System.IO;               // Tipos de I/O como StreamWriter.
using System.Diagnostics;      // Onde fica Process (rodar executáveis externos).
using System.Threading.Tasks;  // Para usar Task.Delay, que é uma forma de esperar sem bloquear a thread principal.

public class AppLauncherOldV2 : MonoBehaviour
{
    // Process instance for the external executable (curl.exe)
    private Process process = null;

    // Standard input stream (kept for compatibility; not actively used here)
    private StreamWriter messageStream;

    [Header("References")]
    public DracoCurlOldV2 DracoCurl;

    /// <summary>
    /// Starts an external process (curl.exe) to download a batch.
    /// This version enforces "one process at a time":
    /// if a previous process is still running, it waits until it finishes.
    /// </summary>
    public async void StartProcess(string AppName, string AppArgs)
    {
        // Wait while a previous curl process is still running
        while (process != null && !process.HasExited)
        {
            await Task.Delay(1);
        }

        try
        {
            process = new Process();

            // NOTE: in this original logic, we don't use Exited events;
            // the batch is advanced based on stdout DataReceived.
            process.EnableRaisingEvents = false;

            // Executable path (persistentDataPath/Executables/curl.exe)
            process.StartInfo.FileName = Application.persistentDataPath + "/Executables/" + AppName;
            process.StartInfo.Arguments = AppArgs;

            // Configure process I/O redirection
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;

            // Run without creating a visible terminal window
            process.StartInfo.CreateNoWindow = true;

            // Hook stdout/stderr events
            process.OutputDataReceived += new DataReceivedEventHandler(DataReceived);  // Quando o processo escrever uma linha em stdout, chama seu método DataReceived.
            process.ErrorDataReceived += new DataReceivedEventHandler(ErrorReceived);  // not main thread

            // Start process
            process.Start();

            // Begin asynchronous reading of stdout
            process.BeginOutputReadLine();

            // Keep stdin handle (not used, but preserved)
            messageStream = process.StandardInput;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Unable to launch app: " + e.Message);
        }
    }

    /// <summary>
    /// Called whenever the process writes a line to stdout.
    /// Original logic uses this as the signal to advance the download batch.
    /// </summary>
    private void DataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        // Handle it (original version just advances batch on stdout activity)
        // UnityEngine.Debug.Log(eventArgs.Data);
        DracoCurl.AdvanceBatch();
    }

    /// <summary>
    /// Called whenever the process writes a line to stderr.
    /// </summary>
    private void ErrorReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        UnityEngine.Debug.LogError(eventArgs.Data);
    }

    /// <summary>
    /// Kills the current process if it is still running.
    /// </summary>
    public void KillProcess()
    {
        if (process != null && !process.HasExited)
        {
            process.Kill();
        }
    }

    private void OnDestroy()
    {
        KillProcess();
    }
}