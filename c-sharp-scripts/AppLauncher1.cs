using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Debug = UnityEngine.Debug;

public class AppLauncher : MonoBehaviour
{
    [Header("Wiring")]
    public DracoCurl DracoCurl;

    [Header("Logging")]
    public bool logStdout = false;
    public bool logStderr = true;
    public bool logCommandLine = true;

    [Header("Process watchdog")]
    [Tooltip("Se > 0, mata o curl se ele não terminar nesse tempo (segundos).")]
    public float processTimeoutSeconds = 0f;

    private readonly List<Process> active = new();

    private struct BatchDone
    {
        public int start;
        public int count;
        public int exitCode;
        public string tag;
        public string reason;     // <- novo: motivo do -1 / falha
    }

    private readonly Queue<BatchDone> doneQueue = new();
    private readonly object doneLock = new();

    /// <summary>
    /// Starts one curl process to download a batch.
    /// </summary>
    public void StartBatch(string appName, string appArgs, int batchStart, int batchCount, string tag = null)
    {
        string exePath = Path.Combine(Application.persistentDataPath, "Executables", appName);
        if (!File.Exists(exePath))
        {
            string msg = $"[AppLauncher] Executable not found: {exePath}";
            Debug.LogError(msg);
            EnqueueDone(batchStart, batchCount, exitCode: -1, tag ?? "missing_exe", reason: msg);
            return;
        }

        if (logCommandLine)
        {
            Debug.Log($"[AppLauncher] Starting batch {batchStart}-{batchStart + batchCount - 1} with command:\n\"{exePath}\" {appArgs}");
        }

        var p = new Process();
        p.StartInfo.FileName = exePath;
        p.StartInfo.Arguments = appArgs;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.EnableRaisingEvents = true;

        var localTag = tag ?? "batch";

        // Optional logging
        p.OutputDataReceived += (s, e) =>
        {
            if (logStdout && !string.IsNullOrWhiteSpace(e.Data))
                Debug.Log($"[curl OUT][{batchStart}-{batchStart + batchCount - 1}] {e.Data}");
        };

        p.ErrorDataReceived += (s, e) =>
        {
            if (logStderr && !string.IsNullOrWhiteSpace(e.Data))
                Debug.LogWarning($"[curl ERR][{batchStart}-{batchStart + batchCount - 1}] {e.Data}");
        };

        // Exited roda fora da main thread
        p.Exited += (s, e) =>
        {
            int code = -1;
            try { code = p.ExitCode; } catch { /* ignore */ }

            EnqueueDone(batchStart, batchCount, code, localTag, reason: "process_exited");
        };

        try
        {
            bool ok = p.Start();
            if (!ok) throw new Exception("Process did not start.");

            Debug.Log($"[AppLauncher] PID={p.Id} started for batch {batchStart}-{batchStart + batchCount - 1}");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            active.Add(p);

            // Watchdog opcional: mata curl travado
            if (processTimeoutSeconds > 0f)
            {
                StartCoroutine(KillIfTimeout(p, batchStart, batchCount, localTag, processTimeoutSeconds));
            }
        }
        catch (Exception ex)
        {
            string msg = $"[AppLauncher] Failed to start process: {ex.Message}";
            Debug.LogError(msg);
            try { p.Dispose(); } catch { }
            EnqueueDone(batchStart, batchCount, exitCode: -1, localTag, reason: msg);
        }
    }

    private System.Collections.IEnumerator KillIfTimeout(Process p, int start, int count, string tag, float timeoutS)
    {
        float t0 = Time.realtimeSinceStartup;
        while (p != null && !p.HasExited)
        {
            if (Time.realtimeSinceStartup - t0 > timeoutS)
            {
                try
                {
                    Debug.LogWarning($"[AppLauncher] TIMEOUT killing PID={p.Id} batch {start}-{start + count - 1}");
                    p.Kill();
                }
                catch { /* ignore */ }

                // sinaliza falha
                EnqueueDone(start, count, exitCode: -1, tag, reason: $"timeout>{timeoutS}s");
                yield break;
            }
            yield return null;
        }
    }

    private void EnqueueDone(int start, int count, int exitCode, string tag, string reason)
    {
        lock (doneLock)
        {
            doneQueue.Enqueue(new BatchDone
            {
                start = start,
                count = count,
                exitCode = exitCode,
                tag = tag,
                reason = reason
            });
        }
    }

    private void Update()
    {
        // Drain completion queue on main thread
        lock (doneLock)
        {
            while (doneQueue.Count > 0)
            {
                var done = doneQueue.Dequeue();
                DracoCurl?.AdvanceBatch(done.start, done.count, done.exitCode, done.tag, done.reason);
            }
        }

        // Cleanup exited processes
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i] == null) { active.RemoveAt(i); continue; }
            if (active[i].HasExited)
            {
                try { active[i].Dispose(); } catch { }
                active.RemoveAt(i);
            }
        }
    }

    public void KillAllProcesses()
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            try
            {
                var p = active[i];
                if (p != null && !p.HasExited) p.Kill();
                p?.Dispose();
            }
            catch { }
        }
        active.Clear();
    }

    private void OnDestroy()
    {
        KillAllProcesses();
    }
}