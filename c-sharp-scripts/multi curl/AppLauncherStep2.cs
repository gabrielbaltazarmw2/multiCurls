using UnityEngine;
using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;

/// <summary>
/// STEP 2 launcher:
/// - Supports multiple concurrent processes (multi-curl).
/// - Each process carries (batchStart, batchCount).
/// - Completion is queued and delivered in Unity main thread via Update().
/// </summary>
public class AppLauncherStep2 : MonoBehaviour
{
    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public DracoMultiCurlStep2 DracoCurl;

    // =========================================================
    // Process bookkeeping
    // =========================================================
    private readonly List<Process> _running = new List<Process>();

    // =========================================================
    // Completion queue (thread-safe enqueue, main-thread dequeue)
    // =========================================================
    private struct Completion
    {
        public int batchStart;
        public int batchCount;
        public int exitCode;
        public string reason;
    }

    private readonly Queue<Completion> _pending = new Queue<Completion>();
    private readonly object _lock = new object();

    /// <summary>
    /// Start one curl process for a batch (no waiting here; Draco controls maxParallelBatches).
    /// </summary>
    public void StartBatch(string appName, string appArgs, int batchStart, int batchCount)
    {
        if (DracoCurl == null)
        {
            Debug.LogError("[AppLauncherStep2] Missing DracoCurl reference.");
            return;
        }

        try
        {
            var p = new Process();

            p.StartInfo.FileName = Path.Combine(Application.persistentDataPath, "Executables", appName);
            p.StartInfo.Arguments = appArgs;

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.CreateNoWindow = true;

            p.EnableRaisingEvents = true;

            // Capture metadata in closure
            p.Exited += (sender, e) =>
            {
                int exit = -1;
                string reason = "";

                try { exit = p.ExitCode; }
                catch (Exception ex) { reason = ex.Message; }

                // Enqueue completion (thread-safe)
                lock (_lock)
                {
                    _pending.Enqueue(new Completion
                    {
                        batchStart = batchStart,
                        batchCount = batchCount,
                        exitCode = exit,
                        reason = reason
                    });
                }

                // Clean up process object
                try { p.Dispose(); } catch { }
            };

            // Optional logs
            p.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.LogError($"[curl][stderr] {e.Data}");
            };

            bool started = p.Start();
            if (!started)
            {
                Debug.LogError("[AppLauncherStep2] Failed to start process.");
                return;
            }

            // Start async read
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            _running.Add(p);
        }
        catch (Exception ex)
        {
            Debug.LogError("[AppLauncherStep2] Unable to launch app: " + ex.Message);
        }
    }

    private void Update()
    {
        // Deliver all pending completions on main thread
        while (true)
        {
            Completion c;
            lock (_lock)
            {
                if (_pending.Count == 0) break;
                c = _pending.Dequeue();
            }

            // notify Draco in main thread
            DracoCurl.AdvanceBatch(c.batchStart, c.batchCount, c.exitCode, c.reason);
        }

        // Remove exited processes from list (best-effort)
        for (int i = _running.Count - 1; i >= 0; i--)
        {
            var p = _running[i];
            if (p == null) { _running.RemoveAt(i); continue; }
            try
            {
                if (p.HasExited) _running.RemoveAt(i);
            }
            catch
            {
                _running.RemoveAt(i);
            }
        }
    }

    public void KillAllProcesses()
    {
        foreach (var p in _running)
        {
            try
            {
                if (p != null && !p.HasExited)
                    p.Kill();
            }
            catch { }
        }
        _running.Clear();
    }

    private void OnDestroy()
    {
        KillAllProcesses();
    }
}