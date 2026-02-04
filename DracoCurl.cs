

using System.Collections.Generic;
using UnityEngine;
using System.Threading.Tasks;
using System.IO;
using System;
using UnityEngine.Networking;
using Draco;
using System.Linq;

public class DracoCurl : MonoBehaviour
{
    private string fullPath;
    public string HostPath;
    private string _http = "https://";
    private string _port = "443";

    [SerializeField] private string[] _files;
    private int currentFiles = 0;

    [SerializeField] private int[] sliceAddressList;
    private int currentSlice = 0;

    public float FPS = 30;
    private float inverseFPS;

    public int batchSize = 30;
    private int loadedFilesMaxSize = 90;

    [Header("Parallel Download")]
    public int maxParallelBatches = 3;
    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = 0;

    private string[] dracoFiles;

    public AppLauncher appLauncher;
    public DracoToParticles particlesScript;

    private Mesh currentMesh;
    private float startTime = 0;

    [SerializeField] private int numberOfFiles = 300;

    // ✅ Buffer indexado: play em ordem garantida
    private Mesh[] decodedMeshes;
    private int decodedCount = 0;

    // Contador de arquivos prontos para decodificar (Downloaded)
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

    private void OnEnable()
    {
        inverseFPS = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        currentMesh = new Mesh();

        // Setup inicial (pode também chamar Reconnect via UI)
        UpdateDracoFiles();
    }

    void UpdateDracoFiles()
    {
        // Lista de arquivos hardcoded
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        // Estados sempre resetados
        filesReadinessStatus = new readiness[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            filesReadinessStatus[i] = readiness.None;

        // Buffer indexado resetado
        if (decodedMeshes != null)
        {
            for (int i = 0; i < decodedMeshes.Length; i++)
            {
                if (decodedMeshes[i] != null) Destroy(decodedMeshes[i]);
                decodedMeshes[i] = null;
            }
        }
        decodedMeshes = new Mesh[numberOfFiles];

        // Controles resetados
        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
        downloadedCount = 0;
        decodedCount = 0;

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        activeBatches = 0;
        nextDownloadIndex = 0;

        playerReady = true;

        // Garante pasta de download
        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));
    }

    public class BypassCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }

    // -------------------------
    // DOWNLOAD: Start batches in parallel
    // -------------------------
    private void TryStartDownloadBatches()
    {
        if (dracoFiles == null) return;

        // back-pressure: se já tem muito Downloaded/Decoded, não dispare mais
        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return; // nada para baixar

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            // Marcar como Downloading para não duplicar
            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            string appArgs = "--http3 --parallel";
            for (int i = start; i < end; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;

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

    /// <summary>
    /// Called by AppLauncher on main thread when a batch finishes.
    /// </summary>
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode)
    {
        activeBatches = Mathf.Max(0, activeBatches - 1);

        // Falhou: libera para retry
        if (exitCode != 0)
        {
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
            {
                // volta pra None (pode tentar baixar de novo)
                filesReadinessStatus[i] = readiness.None;
            }
            Debug.LogWarning($"[AdvanceBatch] Batch {batchStart}-{batchStart + batchCount - 1} FAILED (exitCode={exitCode})");
            return;
        }

        // Sucesso: marca Downloaded
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
        // Debug.Log($"[AdvanceBatch] Batch {batchStart}-{batchStart + batchCount - 1} OK, marked Downloaded={marked}");
    }

    // -------------------------
    // LOAD: Decode out of order
    // -------------------------
    private void TryDecode()
    {
        if (decodedCount >= loadedFilesMaxSize) return;

        // varredura curta para não custar muito por frame
        int scanLimit = Mathf.Min(numberOfFiles, batchSize * 2);
        for (int k = 0; k < scanLimit; k++)
        {
            int i = (currentLoadedNumber + k) % numberOfFiles;

            if (filesReadinessStatus[i] == readiness.Downloaded)
            {
                // reserva esse índice para decodificação
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
            // arquivo ainda não existe/IO falhou -> volta estado
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

            // se já existia mesh nesse slot, destrói
            if (decodedMeshes[index] != null) Destroy(decodedMeshes[index]);
            decodedMeshes[index] = tempMesh;

            filesReadinessStatus[index] = readiness.Loaded;
            decodedCount += 1;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DecodeMeshAtIndex] decode failed idx={index}: {ex.Message}");

            // libera para tentar de novo (ou marque None)
            filesReadinessStatus[index] = readiness.None;
        }
    }

    byte[] ReadStreamFromDownloadedFile(string fileName, string filePath)
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
        else
        {
            // Pode ser normal em bordas/IO, mas se acontecer demais indica race/erro de marcação
            // Debug.LogWarning($"File not found: {full}");
            return null;
        }
    }

    // -------------------------
    // PLAY: Always in order
    // -------------------------
    private async void PlaySingleFile()
    {
        playerReady = false;

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

        // troca mesh atual
        if (currentMesh != null) Destroy(currentMesh);
        currentMesh = decodedMeshes[i];
        decodedMeshes[i] = null;

        decodedCount = Mathf.Max(0, decodedCount - 1);

        // marca consumido
        filesReadinessStatus[i] = readiness.None;

        // avança em ordem
        currentPlayingNumber = (i + 1) % numberOfFiles;

        PlaySingleFile();
    }

    // -------------------------
    // Public controls
    // -------------------------
    public void SetNewIP(string newIP) { HostPath = newIP; ResetHostPath(); }
    public void SetNewPort(string newPort) { _port = newPort; ResetHostPath(); }

    public void SetPortFromSliceList(int slice)
    {
        currentSlice = slice;
        _port = sliceAddressList[currentSlice].ToString();
        ResetHostPath();
    }

    public void SetQualityFromQualityList(int quality) { currentFiles = quality; ResetHostPath(); }

    private void ResetHostPath()
    {
        fullPath = _http + HostPath + ":" + _port + "/" + _files[currentFiles];
        if (!fullPath.EndsWith("/")) fullPath += "/";
    }

    public void Reconnect()
    {
        appLauncher?.KillAllProcesses();
        UpdateDracoFiles();
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (Int32.TryParse(newFramerate, out int checkOutput))
        {
            FPS = checkOutput;
            inverseFPS = 1000f / Mathf.Max(1f, FPS);
        }
    }

    // -------------------------
    // Main loop
    // -------------------------
    private void Update()
    {
        if (dracoFiles == null) return;

        // 1) DOWNLOAD
        TryStartDownloadBatches();

        // 2) LOAD/DECODE
        TryDecode();

        // 3) PLAY
        TryPlay();
    }
}
