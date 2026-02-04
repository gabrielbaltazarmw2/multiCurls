using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Debug = UnityEngine.Debug;

public class MultiCurlDownloadTest : MonoBehaviour
{
    [Header("Server")]
    public string host = "127.0.0.1";
    public string port = "443";
    public string folder = "seq1/"; // ex: "draco/" ou "frames/"

    [Header("Files")]
    public int firstId = 1000;      // arquivo inicial: 1000.drc
    public int numberOfFiles = 120; // total que você quer tentar baixar
    public int batchSize = 30;      // arquivos por curl

    [Header("Concurrency")]
    public int maxParallelBatches = 3;

    [Header("Curl")]
    public string curlExeName = "curl.exe";
    public bool useHttp3 = true;
    public bool useParallelFlag = true;

    private readonly List<Process> active = new();
    private int nextIndex = 0;
    private string baseUrl;
    private string downloadsDir;

    void Start()
    {
        baseUrl = $"https://{host}:{port}/{folder}";
        if (!baseUrl.EndsWith("/")) baseUrl += "/";

        downloadsDir = Path.Combine(Application.persistentDataPath, "DownloadsTest");
        Directory.CreateDirectory(downloadsDir);

        Debug.Log($"[MultiCurlTest] Base URL: {baseUrl}");
        Debug.Log($"[MultiCurlTest] Download dir: {downloadsDir}");

        // dispara logo no Start, mas você pode trocar por botão/UI
        TryStartMoreBatches();
    }

    void Update()
    {
        // limpa processos que terminaram (garantia extra)
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i].HasExited)
            {
                active[i].Dispose();
                active.RemoveAt(i);
            }
        }

        // tenta manter o nível de paralelismo
        TryStartMoreBatches();
    }

    private void TryStartMoreBatches()
    {
        while (active.Count < maxParallelBatches && nextIndex < numberOfFiles)
        {
            int start = nextIndex;
            int end = Mathf.Min(start + batchSize, numberOfFiles);
            nextIndex = end;

            StartBatch(start, end);
        }
    }

    private void StartBatch(int startIndex, int endIndex)
    {
        // monta args
        string args = "";
        if (useHttp3) args += "--http3 ";
        if (useParallelFlag) args += "--parallel ";

        for (int i = startIndex; i < endIndex; i++)
        {
            int fileId = firstId + i;
            string fileName = $"{fileId}.drc";

            string outPath = Path.Combine(downloadsDir, fileName);
            string url = baseUrl + fileName;

            args += $" -o \"{outPath}\" \"{url}\"";
        }

        // inicia process
        var p = new Process();
        var curlPath = Path.Combine(Application.persistentDataPath, "Executables", curlExeName);
        if (!File.Exists(curlPath))
        {
            Debug.LogError($"curl not found: {curlPath}");
            return;
        }
        p.StartInfo.FileName = curlPath;
        p.StartInfo.Arguments = args;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;
        p.EnableRaisingEvents = true;

        var sw = Stopwatch.StartNew();
        int batchFiles = endIndex - startIndex;

        p.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Debug.Log($"[curl OUT][{startIndex}-{endIndex - 1}] {e.Data}");
        };

        p.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Debug.LogWarning($"[curl ERR][{startIndex}-{endIndex - 1}] {e.Data}");
        };

        p.Exited += (s, e) =>
        {
            sw.Stop();
            Debug.Log($"[Batch DONE] idx {startIndex}-{endIndex - 1} ({batchFiles} files) in {sw.ElapsedMilliseconds} ms | active={active.Count - 1}");
        };

        try
        {
            bool ok = p.Start();
            if (!ok) throw new Exception("Process didn't start.");

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            active.Add(p);

            Debug.Log($"[Batch START] idx {startIndex}-{endIndex - 1} ({batchFiles} files) | active={active.Count}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Batch FAIL] idx {startIndex}-{endIndex - 1}: {ex.Message}");
            try { p.Dispose(); } catch { }
        }
    }

    private void OnDestroy()
    {
        foreach (var p in active)
        {
            try
            {
                if (!p.HasExited) p.Kill();
                p.Dispose();
            }
            catch { }
        }
        active.Clear();
    }
}
