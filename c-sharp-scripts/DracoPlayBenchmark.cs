using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Draco;
using Debug = UnityEngine.Debug;


public class DracoPlayBenchmark : MonoBehaviour
{
    [Header("Input files (.drc)")]
    [Tooltip("Se verdadeiro, usa Application.persistentDataPath como base.")]
    public bool usePersistentDataPath = false;

    [Tooltip("Pasta relativa (se usePersistentDataPath = true) ou caminho absoluto dos .drc.")]
    public string inputFolder = @"C:\Users\Rafael\Desktop\PointClouds\Longdress\draco\draco-qp10";

    [Tooltip("Padrão de busca dos arquivos .drc")]
    public string searchPattern = "*.drc";

    [Tooltip("Limitar o número de arquivos (0 = todos).")]
    public int maxFiles = 0;

    [Header("Player")]
    [Tooltip("FPS alvo para o player.")]
    public float targetFPS = 30f;

    [Tooltip("Loopar a sequência de meshes.")]
    public bool loopPlayback = true;

    [Tooltip("Script DracoToParticles que controla o VFX (PCVFX).")]
    public DracoToParticles particlesScript;

    [Header("Logging")]
    public bool logToFile = true;

    [Tooltip("Subpasta extra dentro de 'play_logs' para organizar experimentos.")]
    public string extraLogFolder = "";

    private string logDir;
    private string logFilePath;
    private readonly object logLock = new object();

    private readonly List<Mesh> decodedMeshes = new List<Mesh>();
    private bool playbackReady = false;

    private float frameInterval;
    private int frameIndex = 0;

    private void Awake()
    {
        if (particlesScript == null)
        {
            particlesScript = FindObjectOfType<DracoToParticles>();
            if (particlesScript == null)
            {
                Debug.LogError("[PlayBenchmark] DracoToParticles não encontrado na cena.");
            }
        }
    }

    private async void Start()
    {
        frameInterval = 1f / targetFPS;

        // 1) Resolver pasta de entrada
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
            Debug.LogError($"[PlayBenchmark] Input folder not found: {folderPath}");
            return;
        }

        // 2) Encontrar arquivos .drc
        var files = Directory.GetFiles(folderPath, searchPattern, SearchOption.TopDirectoryOnly)
                             .OrderBy(f => f)
                             .ToList();

        if (files.Count == 0)
        {
            Debug.LogError($"[PlayBenchmark] No files found in {folderPath} with pattern {searchPattern}");
            return;
        }

        if (maxFiles > 0 && maxFiles < files.Count)
        {
            files = files.Take(maxFiles).ToList();
        }

        SetupLogging();

        WriteLog("=== DracoPlayBenchmark started ===");
        WriteLog($"Input folder: {folderPath}");
        WriteLog($"Files found: {files.Count}");
        WriteLog($"Target FPS: {targetFPS}");

        Debug.Log($"[PlayBenchmark] Decoding {files.Count} files before playback...");

        var globalSw = Stopwatch.StartNew();

        // 3) Decodificar todos os arquivos uma vez só
        int idx = 0;
        foreach (var filePath in files)
        {
            idx++;
            Mesh m = await DecodeSingleFile(filePath, idx, files.Count);
            if (m != null)
            {
                decodedMeshes.Add(m);
            }
        }

        globalSw.Stop();
        WriteLog($"[DECODE_SUMMARY] Total decode time (ms): {globalSw.Elapsed.TotalMilliseconds:F3}");
        WriteLog($"[DECODE_SUMMARY] Decoded meshes: {decodedMeshes.Count}");

        if (decodedMeshes.Count == 0)
        {
            Debug.LogError("[PlayBenchmark] Nenhuma mesh decodificada, abortando playback.");
            WriteLog("[ERROR] No decoded meshes available.");
            return;
        }

        playbackReady = true;
        Debug.Log("[PlayBenchmark] Decode finished, starting playback benchmark...");
        WriteLog("=== Playback loop starting ===");

        // 4) Iniciar loop de play
        StartCoroutine(PlaybackLoop());
    }

    private void SetupLogging()
    {
        if (!logToFile)
        {
            logFilePath = null;
            return;
        }

        logDir = Path.Combine(Application.persistentDataPath, "play_logs");

        if (!string.IsNullOrWhiteSpace(extraLogFolder))
        {
            logDir = Path.Combine(logDir, extraLogFolder);
        }

        Directory.CreateDirectory(logDir);

        logFilePath = Path.Combine(
            logDir,
            $"play_benchmark_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
        );
    }

    private async Task<Mesh> DecodeSingleFile(string filePath, int index, int total)
    {
        string fileName = Path.GetFileName(filePath);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayBenchmark] Error reading file {fileName}: {ex.Message}");
            WriteLog($"[ERROR] reading {fileName}: {ex.Message}");
            return null;
        }

        var meshDataArray = Mesh.AllocateWritableMeshData(1);

        var swDecode = Stopwatch.StartNew();
        try
        {
            await DracoDecoder.DecodeMesh(meshDataArray[0], bytes);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayBenchmark] Error decoding {fileName}: {ex.Message}");
            WriteLog($"[ERROR] decoding {fileName}: {ex.Message}");
            meshDataArray.Dispose();
            return null;
        }
        swDecode.Stop();

        Mesh mesh = new Mesh();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);

        double decodeMs = swDecode.Elapsed.TotalMilliseconds;
        string msg = $"[DECODE] {index}/{total} file={fileName} decode_ms={decodeMs:F3}";
        Debug.Log(msg);
        WriteLog(msg);

        await Task.Yield();
        return mesh;
    }

    private System.Collections.IEnumerator PlaybackLoop()
    {
        if (!playbackReady || particlesScript == null)
        {
            Debug.LogError("[PlayBenchmark] PlaybackLoop started without readiness.");
            yield break;
        }

        float targetInterval = frameInterval; // em segundos
        float lastFrameTime = Time.realtimeSinceStartup;

        int meshCount = decodedMeshes.Count;

        while (true)
        {
            // Seleciona mesh atual
            int meshIndex = frameIndex % meshCount;
            Mesh currentMesh = decodedMeshes[meshIndex];

            // Aplica no VFX
            particlesScript.Set(currentMesh);

            float now = Time.realtimeSinceStartup;
            float delta = (now - lastFrameTime) * 1000f; // ms
            lastFrameTime = now;

            string msg = $"[PLAY] frame={frameIndex} meshIndex={meshIndex} delta_ms={delta:F3}";
            Debug.Log(msg);
            WriteLog(msg);

            frameIndex++;

            // Se não for loopar e chegou no fim, encerra
            if (!loopPlayback && frameIndex >= meshCount)
            {
                WriteLog("=== Playback loop finished (no loop) ===");
                yield break;
            }

            // Espera até o próximo frame (tempo em tempo real, não escalado)
            yield return new WaitForSecondsRealtime(targetInterval);
        }
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
            // não interrompe o benchmark por erro de IO
        }
    }
}
