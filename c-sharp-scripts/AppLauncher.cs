using UnityEngine;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;

/// <summary>
/// Manages a pool of external processes (curl instances) for parallel downloads.
/// Each slot in the pool can run one process at a time.
/// </summary>
public class AppLauncher : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Configuration
    // ─────────────────────────────────────────────────────────────

    [Header("Process Pool")]
    [Tooltip("Maximum number of processes that can run simultaneously.")]
    [SerializeField] private int poolSize = 4;

    // ─────────────────────────────────────────────────────────────
    //  Internal state
    // ─────────────────────────────────────────────────────────────

    private Process[] pool;
    private StreamWriter[] messageStreams;
    private bool[] slotBusy;

    // Callback invoked when a slot finishes: Action<slotIndex>
    private Action<int>[] onSlotFinished;

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────

    public int PoolSize => poolSize;

    private void Awake()
    {
        InitPool();
    }

    /// <summary>
    /// Initializes (or re-initializes) the process pool with the current poolSize.
    /// </summary>
    public void InitPool()
    {
        KillAll();

        pool            = new Process[poolSize];
        messageStreams   = new StreamWriter[poolSize];
        slotBusy        = new bool[poolSize];
        onSlotFinished  = new Action<int>[poolSize];

        Debug.Log($"[AppLauncher] Pool initialized with {poolSize} slots.");
    }

    /// <summary>
    /// Launches a process in the first available slot.
    /// </summary>
    /// <param name="slotIndex">Which pool slot to use.</param>
    /// <param name="appName">Executable name (looked up inside the Executables folder).</param>
    /// <param name="appArgs">Arguments to pass to the executable.</param>
    /// <param name="onFinished">Callback invoked when the process writes to stdout (batch done signal).</param>
    public async void StartProcess(int slotIndex, string appName, string appArgs, Action<int> onFinished)
    {
        if (slotIndex < 0 || slotIndex >= poolSize)
        {
            Debug.LogError($"[AppLauncher] Invalid slot index {slotIndex}.");
            return;
        }

        // Wait for a previous process on this slot to finish
        while (pool[slotIndex] != null && !pool[slotIndex].HasExited)
        {
            await Task.Delay(5);
        }

        onSlotFinished[slotIndex] = onFinished;

        try
        {
            var p = new Process();
            p.EnableRaisingEvents = false;
            p.StartInfo.FileName               = Application.persistentDataPath + "/Executables/" + appName;
            p.StartInfo.Arguments              = appArgs;
            p.StartInfo.UseShellExecute        = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardInput  = true;
            p.StartInfo.RedirectStandardError  = true;
            p.StartInfo.CreateNoWindow         = true;

            // Capture slot index for use inside the lambda
            int capturedSlot = slotIndex;
            p.OutputDataReceived += (sender, e) => OnDataReceived(capturedSlot, e);
            p.ErrorDataReceived  += (sender, e) => OnErrorReceived(capturedSlot, e);

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            pool[slotIndex]           = p;
            messageStreams[slotIndex] = p.StandardInput;
            slotBusy[slotIndex]       = true;

            Debug.Log($"[AppLauncher] Slot {slotIndex} started process '{appName}'.");
        }
        catch (Exception e)
        {
            slotBusy[slotIndex] = false;
            Debug.LogError($"[AppLauncher] Slot {slotIndex} failed to launch '{appName}': {e.Message}");
        }
    }

    /// <summary>
    /// Returns true if the given slot is available (not running a process).
    /// </summary>
    public bool IsSlotFree(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= poolSize) return false;
        return pool[slotIndex] == null || pool[slotIndex].HasExited;
    }

    /// <summary>
    /// Kills the process running in the specified slot.
    /// </summary>
    public void KillSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= poolSize) return;
        if (pool[slotIndex] != null && !pool[slotIndex].HasExited)
        {
            pool[slotIndex].Kill();
            Debug.Log($"[AppLauncher] Slot {slotIndex} process killed.");
        }
        slotBusy[slotIndex] = false;
    }

    /// <summary>
    /// Kills all running processes in the pool.
    /// </summary>
    public void KillAll()
    {
        if (pool == null) return;
        for (int i = 0; i < pool.Length; i++)
        {
            KillSlot(i);
        }
        Debug.Log("[AppLauncher] All processes killed.");
    }

    // ─────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────

    private void OnDataReceived(int slotIndex, DataReceivedEventArgs e)
    {
        // curl outputs a line when the transfer finishes – treat any output as "batch done"
        if (string.IsNullOrEmpty(e.Data)) return;

        slotBusy[slotIndex] = false;
        onSlotFinished[slotIndex]?.Invoke(slotIndex);
    }

    private void OnErrorReceived(int slotIndex, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            Debug.LogWarning($"[AppLauncher] Slot {slotIndex} stderr: {e.Data}");
    }

    private void OnDestroy()
    {
        KillAll();
    }
}
