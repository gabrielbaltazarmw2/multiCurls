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

    [Header("Logging (optional)")]
    public bool logStdout = false;
    public bool logStderr = true;
    public bool logCommandLine = true;

    private readonly List<Process> active = new();

    private struct BatchDone
    {
        public int start;
        public int count;
        public int exitCode;
        public string tag;
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
            Debug.LogError($"[AppLauncher] Executable not found: {exePath}");
            EnqueueDone(batchStart, batchCount, exitCode: -1, tag ?? "missing_exe");
            return;
        }

        if (logCommandLine)
        {
            Debug.Log($"[AppLauncher] Starting batch {batchStart}-{batchStart + batchCount - 1} " +
                      $"with command:\n\"{exePath}\" {appArgs}");
        }

        var p = new Process();
        p.StartInfo.FileName = exePath;
        p.StartInfo.Arguments = appArgs;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.EnableRaisingEvents = true;

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

            EnqueueDone(batchStart, batchCount, code, tag ?? "batch");
        };

        try
        {
            bool ok = p.Start();
            if (!ok) throw new Exception("Process did not start.");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            active.Add(p);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AppLauncher] Failed to start process: {ex.Message}");
            try { p.Dispose(); } catch { }
            EnqueueDone(batchStart, batchCount, exitCode: -1, tag ?? "start_fail");
        }
    }

    private void EnqueueDone(int start, int count, int exitCode, string tag)
    {
        lock (doneLock)
        {
            doneQueue.Enqueue(new BatchDone
            {
                start = start,
                count = count,
                exitCode = exitCode,
                tag = tag
            });
        }
    }

    private void Update()
    {
        // Processar batches concluídos na main thread
        lock (doneLock)
        {
            while (doneQueue.Count > 0)
            {
                var done = doneQueue.Dequeue();
                DracoCurl?.AdvanceBatch(done.start, done.count, done.exitCode);
            }
        }

        // Cleanup dos processos
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i] == null)
            {
                active.RemoveAt(i);
                continue;
            }

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
