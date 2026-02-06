using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Draco;
using Debug = UnityEngine.Debug;

public class DracoDecodeBenchmark : MonoBehaviour
{
    [Header("Input files")]
    [Tooltip("Se verdadeiro, usa Application.persistentDataPath como base.")]
    public bool usePersistentDataPath = true;

    [Tooltip("Pasta relativa (a partir de persistentDataPath) OU caminho absoluto, dependendo de usePersistentDataPath.")]
    public string inputFolder = "DownloadsTest";

    [Tooltip("Padrão de busca dos arquivos .drc")]
    public string searchPattern = "*.drc";

    [Header("Decode options")]
    [Tooltip("Limitar o número de arquivos (0 = todos).")]
    public int maxFiles = 0;

    [Tooltip("Destruir a mesh após decodificar (economiza memória).")]
    public bool destroyMeshAfterDecode = true;

    [Header("Logging")]
    public bool logToFile = true;

    [Tooltip("Subpasta extra dentro de 'decode_logs' para organizar experimentos (ex: 'seq1' ou 'draco_700mhz').")]
    public string extraLogFolder = "";

    private string logDir;
    private string logFilePath;
    private readonly object logLock = new object();

    // Lista de tempos de decode (ms) por arquivo
    private readonly List<double> decodeTimesMs = new List<double>();
    private readonly List<double> totalTimesMs = new List<double>();

    async void Start()
    {
        // 1) Resolver caminho da pasta de entrada
        string folderPath;
        if (usePersistentDataPath)
        {
            folderPath = Path.Combine(Application.persistentDataPath, inputFolder);
        }
        else
        {
            folderPath = inputFolder;
        }

        if (!Directory.Exists(folderPath))
        {
            Debug.LogError($"[DecodeBenchmark] Input folder not found: {folderPath}");
            return;
        }

        // 2) Descobrir arquivos .drc
        var files = Directory.GetFiles(folderPath, searchPattern, SearchOption.TopDirectoryOnly)
                             .OrderBy(f => f)
                             .ToList();

        if (files.Count == 0)
        {
            Debug.LogError($"[DecodeBenchmark] No files found in {folderPath} with pattern {searchPattern}");
            return;
        }

        if (maxFiles > 0 && maxFiles < files.Count)
        {
            files = files.Take(maxFiles).ToList();
        }

        SetupLogging();

        WriteLog("=== DracoDecodeBenchmark started ===");
        WriteLog($"Input folder: {folderPath}");
        WriteLog($"Files found: {files.Count}");
        WriteLog($"Destroy mesh after decode: {destroyMeshAfterDecode}");
        Debug.Log($"[DecodeBenchmark] Starting decode of {files.Count} files...");

        var globalSw = Stopwatch.StartNew();

        // 3) Loop de decodificação sequencial
        int index = 0;
        foreach (var filePath in files)
        {
            index++;
            await DecodeSingleFile(filePath, index, files.Count);
        }

        globalSw.Stop();

        // 4) Estatísticas
        WriteStatistics(globalSw.Elapsed.TotalMilliseconds);

        Debug.Log("[DecodeBenchmark] Finished. See log file for details:");
        Debug.Log(logFilePath);
        WriteLog("=== DracoDecodeBenchmark finished ===");
    }

    private void SetupLogging()
    {
        if (!logToFile)
        {
            logFilePath = null;
            return;
        }

        logDir = Path.Combine(Application.persistentDataPath, "decode_logs");

        if (!string.IsNullOrWhiteSpace(extraLogFolder))
        {
            logDir = Path.Combine(logDir, extraLogFolder);
        }

        Directory.CreateDirectory(logDir);

        logFilePath = Path.Combine(
            logDir,
            $"decode_benchmark_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
        );
    }

    private async Task DecodeSingleFile(string filePath, int index, int total)
    {
        string fileName = Path.GetFileName(filePath);

        // Medir tempo total (ler arquivo + decode + aplicar mesh)
        var swTotal = Stopwatch.StartNew();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecodeBenchmark] Error reading file {fileName}: {ex.Message}");
            WriteLog($"[ERROR] reading {fileName}: {ex.Message}");
            return;
        }

        // Alocar buffer de mesh
        var meshDataArray = Mesh.AllocateWritableMeshData(1);

        var swDecode = Stopwatch.StartNew();
        try
        {
            // Decodificação Draco
            await DracoDecoder.DecodeMesh(meshDataArray[0], bytes);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecodeBenchmark] Error decoding {fileName}: {ex.Message}");
            WriteLog($"[ERROR] decoding {fileName}: {ex.Message}");
            // Liberar buffer mesmo com erro
            meshDataArray.Dispose();
            return;
        }
        swDecode.Stop();

        // Criar mesh Unity
        Mesh mesh = new Mesh();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);

        if (destroyMeshAfterDecode)
        {
            UnityEngine.Object.Destroy(mesh);
        }

        swTotal.Stop();

        double decodeMs = swDecode.Elapsed.TotalMilliseconds;
        double totalMs = swTotal.Elapsed.TotalMilliseconds;

        decodeTimesMs.Add(decodeMs);
        totalTimesMs.Add(totalMs);

        string msg = $"[DECODE] {index}/{total} file={fileName} decode_ms={decodeMs:F3} total_ms={totalMs:F3}";
        Debug.Log(msg);
        WriteLog(msg);

        // Pequena pausa opcional, só pra não travar o editor se tiver muitos arquivos
        await Task.Yield();
    }

    private void WriteStatistics(double totalElapsedMs)
    {
        if (decodeTimesMs.Count == 0)
        {
            WriteLog("[STATS] No decode times recorded.");
            return;
        }

        double avgDecode = decodeTimesMs.Average();
        double medianDecode = Percentile(decodeTimesMs, 50);
        double p95Decode = Percentile(decodeTimesMs, 95);

        double avgTotal = totalTimesMs.Average();

        WriteLog("=== Statistics (Decode) ===");
        WriteLog($"Files decoded: {decodeTimesMs.Count}");
        WriteLog($"Total wall-clock time (ms): {totalElapsedMs:F3}");
        WriteLog($"Avg decode_ms: {avgDecode:F3}");
        WriteLog($"Median decode_ms: {medianDecode:F3}");
        WriteLog($"P95 decode_ms: {p95Decode:F3}");
        WriteLog($"Avg total_ms (read+decode+mesh): {avgTotal:F3}");
    }

    private double Percentile(List<double> values, double percentile)
    {
        if (values == null || values.Count == 0)
            return 0.0;

        var sorted = values.OrderBy(v => v).ToList();
        if (percentile <= 0) return sorted.First();
        if (percentile >= 100) return sorted.Last();

        double position = (sorted.Count + 1) * percentile / 100.0;
        int index = (int)position;

        if (index <= 0) return sorted[0];
        if (index >= sorted.Count) return sorted[sorted.Count - 1];

        double fraction = position - index;
        return sorted[index - 1] + fraction * (sorted[index] - sorted[index - 1]);
    }

    private void WriteLog(string message)
    {
        if (!logToFile || string.IsNullOrEmpty(logFilePath))
        {
            return;
        }

        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (logLock)
            {
                File.AppendAllText(logFilePath, line);
            }
        }
        catch
        {
            // não deixa o benchmark quebrar por falha de IO
        }
    }
}
