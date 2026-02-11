using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoCurl : MonoBehaviour
{
    // -------------------------
    // Server / paths
    // -------------------------
    [Header("Server")]
    public string HostPath = "ateixs.me";
    private const string _http = "https://";
    private string _port = "443";
    private string fullPath;              // ex: "https://ateixs.me:443/draco/"

    [Header("Content paths")]
    [Tooltip("Lista de subpastas de conteúdo no servidor (ex: \"draco/\").")]
    [SerializeField] private string[] _files;  // conteúdo: "draco/", "seq1/", etc.
    [Tooltip("Índice dentro de _files que está sendo usado.")]
    [SerializeField] private int currentFiles = 0;

    [Header("Slices / Ports")]
    [Tooltip("Lista de portas/slices disponíveis (ex: 5002, 443, 5001).")]
    [SerializeField] private int[] sliceAddressList;
    private int currentSlice = 0;

    // -------------------------
    // Playback
    // -------------------------
    [Header("Playback")]
    public float FPS = 30f;
    private float inverseFPS;     // alvo em ms (1000 / FPS)

    public int batchSize = 30;    // nº de arquivos por batch de download
    private int loadedFilesMaxSize = 90;

    [Header("Parallel Download")]
    public int maxParallelBatches = 3;
    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    // -------------------------
    // Sequence / state
    // -------------------------
    [Header("References")]
    public AppLauncher appLauncher;
    public DracoToParticles particlesScript;

    [Header("Sequence config")]
    [SerializeField] private int numberOfFiles = 300;

    // buffer indexado: 0..numberOfFiles-1
    private string[] dracoFiles;
    private Mesh[] decodedMeshes;

    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = 0;
    private Mesh currentMesh;
    private float startTime = 0f;

    private int decodedCount = 0;
    private int downloadedCount = 0;
    private bool playerReady = true;

    private enum readiness
    {
        None,
        Downloading,
        Downloaded,
        Decoding,
        Loaded
    }

    [SerializeField] private readiness[] filesReadinessStatus;

    // =========================================================
    // Lifecycle
    // =========================================================

    private void Awake()
    {
        // Validação mínima para evitar Null/Index errors
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoCurl] _files está vazio. Configure pelo menos um path (ex: \"draco/\") no Inspector.");
            enabled = false;
            return;
        }

        if (currentFiles < 0 || currentFiles >= _files.Length)
            currentFiles = 0;

        if (string.IsNullOrEmpty(HostPath))
            HostPath = "localhost";

        ResetHostPath(); // monta fullPath = https://HostPath:port/files[currentFiles]
    }

    private void OnEnable()
    {
        inverseFPS = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        currentMesh = new Mesh();

        UpdateDracoFiles();
    }

    private void OnDisable()
    {
        appLauncher?.KillAllProcesses();
    }

    // =========================================================
    // Setup inicial
    // =========================================================
    private void UpdateDracoFiles()
    {
        // 1) lista de nomes de arquivos (1000.drc ... )
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        // 2) estados
        filesReadinessStatus = new readiness[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            filesReadinessStatus[i] = readiness.None;

        // 3) buffer de meshes
        if (decodedMeshes != null)
        {
            for (int i = 0; i < decodedMeshes.Length; i++)
            {
                if (decodedMeshes[i] != null) Destroy(decodedMeshes[i]);
                decodedMeshes[i] = null;
            }
        }
        decodedMeshes = new Mesh[numberOfFiles];

        // 4) contadores
        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
        downloadedCount = 0;
        decodedCount = 0;

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        activeBatches = 0;
        nextDownloadIndex = 0;

        playerReady = true;

        // 5) pasta de Downloads
        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        Debug.Log($"[DracoCurl] Inicializado. fullPath={fullPath}, numberOfFiles={numberOfFiles}, loadedFilesMaxSize={loadedFilesMaxSize}");
    }

    // =========================================================
    // DOWNLOAD
    // =========================================================
    private void TryStartDownloadBatches()
    {
        if (dracoFiles == null) return;
        if (string.IsNullOrEmpty(fullPath))
        {
            Debug.LogError("[DracoCurl] fullPath vazio. Verifique HostPath / _files no Inspector.");
            return;
        }

        // se já temos muitos arquivos prontos, não baixar mais
        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return;

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            // marca como Downloading
            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            string appArgs = "--http3 --parallel";
            for (int i = start; i < end; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;  // fullPath já termina com "/"

                appArgs += $" -o \"{outPath}\" \"{url}\"";
            }

            activeBatches++;
            nextDownloadIndex = (end < numberOfFiles) ? end : 0;

            appLauncher.StartBatch("curl.exe", appArgs, start, count, tag: "dl");
        }
    }

    private int FindNextNoneIndex(int startFrom)
    {
        for (int k = 0; k < numberOfFiles; k++)
        {
            int i = (startFrom + k) % numberOfFiles;
            if (filesReadinessStatus[i] == readiness.None)
                return i;
        }
        return -1;
    }

    /// Chamado pelo AppLauncher (no main thread) quando um batch termina.
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode)
    {
        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            // falha: volta pra None para poder tentar de novo
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            Debug.LogWarning($"[DracoCurl] Batch {batchStart}-{batchStart + batchCount - 1} FAILED (exitCode={exitCode})");
            return;
        }

        // sucesso: marca Downloaded
        int marked = 0;
        for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
        {
            if (filesReadinessStatus[i] == readiness.Downloading)
            {
                filesReadinessStatus[i] = readiness.Downloaded;
                marked++;
            }
        }

        downloadedCount += marked;
        // Debug.Log($"[DracoCurl] Batch {batchStart}-{batchStart + batchCount - 1} OK, Downloaded={marked}");
    }

    // =========================================================
    // DECODE
    // =========================================================
    private void TryDecode()
    {
        if (decodedCount >= loadedFilesMaxSize) return;

        int scanLimit = Mathf.Min(numberOfFiles, batchSize * 2);
        for (int k = 0; k < scanLimit; k++)
        {
            int i = (currentLoadedNumber + k) % numberOfFiles;

            if (filesReadinessStatus[i] == readiness.Downloaded)
            {
                filesReadinessStatus[i] = readiness.Decoding;
                downloadedCount = Mathf.Max(0, downloadedCount - 1);

                currentLoadedNumber = (i + 1) % numberOfFiles;

                DecodeMeshAtIndex(i);
                break;
            }
        }
    }

    private async void DecodeMeshAtIndex(int index)
    {
        string fileName = dracoFiles[index];
        string dir = Path.Combine(Application.persistentDataPath, "Downloads");
        byte[] stream = ReadStreamFromDownloadedFile(fileName, dir);

        if (stream == null)
        {
            filesReadinessStatus[index] = readiness.Downloaded;
            downloadedCount += 1;
            await Task.Delay(1);
            return;
        }

        try
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

            Mesh tempMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

            if (decodedMeshes[index] != null) Destroy(decodedMeshes[index]);
            decodedMeshes[index] = tempMesh;

            filesReadinessStatus[index] = readiness.Loaded;
            decodedCount += 1;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecodeMeshAtIndex] decode failed idx={index}: {ex.Message}");
            filesReadinessStatus[index] = readiness.None;
        }
    }

    private byte[] ReadStreamFromDownloadedFile(string fileName, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            filePath = Path.Combine(Application.persistentDataPath, "Downloads");

        string full = Path.Combine(filePath, fileName);

        if (File.Exists(full))
        {
            try { return File.ReadAllBytes(full); }
            catch (Exception ex)
            {
                Debug.LogError($"Error reading file {full}: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    // =========================================================
    // PLAY
    // =========================================================
    private async void PlaySingleFile()
    {
        playerReady = false;

        if (currentMesh != null && particlesScript != null)
            particlesScript.Set(currentMesh);

        float elapsedS = Time.realtimeSinceStartup - startTime;
        float elapsedMS = elapsedS * 1000f;

        if (elapsedMS < inverseFPS)
        {
            int delay = Mathf.Max(1, (int)Math.Round(inverseFPS - elapsedMS));
            await Task.Delay(delay);
        }
        else
        {
            await Task.Delay(1);
        }

        startTime = Time.realtimeSinceStartup;
        playerReady = true;
    }

    private void TryPlay()
    {
        int i = currentPlayingNumber;

        if (!playerReady) return;
        if (filesReadinessStatus[i] != readiness.Loaded) return;
        if (decodedMeshes[i] == null) return;

        playerReady = false;

        if (currentMesh != null) Destroy(currentMesh);
        currentMesh = decodedMeshes[i];
        decodedMeshes[i] = null;

        decodedCount = Mathf.Max(0, decodedCount - 1);
        filesReadinessStatus[i] = readiness.None;

        currentPlayingNumber = (i + 1) % numberOfFiles;

        PlaySingleFile();
    }

    // =========================================================
    // Public controls (UI)
    // =========================================================
    public void SetNewIP(string newIP)
    {
        HostPath = newIP;
        ResetHostPath();
    }

    public void SetNewPort(string newPort)
    {
        _port = newPort;
        ResetHostPath();
    }

    public void SetPortFromSliceList(int slice)
    {
        currentSlice = Mathf.Clamp(slice, 0, sliceAddressList.Length - 1);
        _port = sliceAddressList[currentSlice].ToString();
        ResetHostPath();
    }

    public void SetQualityFromQualityList(int quality)
    {
        if (_files == null || _files.Length == 0) return;
        currentFiles = Mathf.Clamp(quality, 0, _files.Length - 1);
        ResetHostPath();
    }

    private void ResetHostPath()
    {
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoCurl] _files está vazio ao chamar ResetHostPath.");
            fullPath = null;
            return;
        }

        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);

        fullPath = _http + HostPath + ":" + _port + "/" + _files[currentFiles];
        if (!fullPath.EndsWith("/")) fullPath += "/";

        Debug.Log($"[DracoCurl] fullPath set to: {fullPath}");
    }

    public void Reconnect()
    {
        appLauncher?.KillAllProcesses();
        UpdateDracoFiles();
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (int.TryParse(newFramerate, out int outFps))
        {
            FPS = outFps;
            inverseFPS = 1000f / Mathf.Max(1f, FPS);
        }
    }

    // =========================================================
    // Main loop
    // =========================================================
    private void Update()
    {
        if (!enabled || dracoFiles == null) return;

        TryStartDownloadBatches();
        TryDecode();
        TryPlay();
    }
}
