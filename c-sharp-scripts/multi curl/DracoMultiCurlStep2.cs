using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoMultiCurlStep2 : MonoBehaviour
{
    // =========================================================
    // Server / URL configuration
    // =========================================================
    [Header("Server")]
    public string HostPath;
    [SerializeField] private string[] _files;   // content subpaths (e.g. "draco/quality1/")
    [SerializeField] private int currentFiles = 0;

    [Header("Slices / Ports")]
    [SerializeField] private int[] sliceAddressList;
    private int currentSlice = 0;

    private const string HttpPrefix = "https://";
    private string _port = "443";
    private string fullPath;

    // =========================================================
    // Playback / Buffer
    // =========================================================
    [Header("Playback")]
    public float FPS = 30f;
    private float inverseFPSms;
    public bool isLoop = true;

    [Header("Buffering")]
    public int batchSize = 30;
    private int loadedFilesMaxSize = 90;
    [SerializeField] private int numberOfFiles = 300;

    // =========================================================
    // Parallel Download (STEP 2)
    // =========================================================
    [Header("Parallel Download (Step 2)")]
    [Tooltip("Set to 1 to match Step 1 behavior. Increase to 2/3 after validating.")]
    public int maxParallelBatches = 2;

    private int activeBatches = 0;     // how many curl processes currently running
    private int nextDownloadIndex = 0; // scheduler pointer for finding next None slot

    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public AppLauncherStep2 appLauncher;
    public DracoToParticles particlesScript;
    public AnimationFPSCounter counter;

    // =========================================================
    // Internal state
    // =========================================================
    private string[] dracoFiles;
    private Queue<Mesh> loadedMeshes;
    private Mesh currentMesh;
    private float startTime = 0f;

    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = 0;

    private bool playerReady = true;

    // downloadedCount counts how many indices are currently in Downloaded state
    private int downloadedCount = 0;

    // STEP 2 states: add Downloading to make parallelism visible & safe
    private enum readiness { None, Downloading, Downloaded, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    private bool _isSwitchingSlice = false;
    private int _sessionId = 0; // incrementa a cada troca "hard" para invalidar tasks antigas

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void OnEnable()
    {
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoMultiCurlStep2] _files is empty.");
            enabled = false;
            return;
        }
        if (appLauncher == null)
        {
            Debug.LogError("[DracoMultiCurlStep2] appLauncher reference is missing.");
            enabled = false;
            return;
        }
        if (particlesScript == null)
        {
            Debug.LogError("[DracoMultiCurlStep2] particlesScript reference is missing.");
            enabled = false;
            return;
        }

        loadedMeshes = new Queue<Mesh>();
        currentMesh = new Mesh();

        inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        ResetHostPath();
        UpdateDracoFiles();

        // pointers
        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        // download scheduler
        activeBatches = 0;
        nextDownloadIndex = 0;

        downloadedCount = 0;
        playerReady = true;
    }

    // =========================================================
    // Setup helpers
    // =========================================================
    private void UpdateDracoFiles()
    {
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        if (filesReadinessStatus == null || filesReadinessStatus.Length != numberOfFiles)
            filesReadinessStatus = new readiness[numberOfFiles];

        for (int i = 0; i < numberOfFiles; i++)
            filesReadinessStatus[i] = readiness.None;

        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
    }

    private void ResetHostPath()
    {
        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);
        fullPath = $"{HttpPrefix}{HostPath}:{_port}/{_files[currentFiles]}";
        if (!fullPath.EndsWith("/")) fullPath += "/";
    }

    // =========================================================
    // Download scheduling (STEP 2)
    // =========================================================
    private void TryStartDownloadBatches()
    {
        if (dracoFiles == null) return;

        // Keep the same backpressure spirit: don't overfill the "Downloaded" buffer.
        // NOTE: this doesn't count Downloading in-flight; it's minimal and works for Step 2 testing.
        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return;

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            // Mark range as Downloading immediately
            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            // Build curl args for this batch
            string args = "--http3 --parallel";
            for (int i = start; i < end; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;

                // quoting paths is safer; we keep consistent style
                args += $" -o \"{outPath}\" \"{url}\"";
            }

            activeBatches++;
            nextDownloadIndex = (end < numberOfFiles) ? end : 0;

            appLauncher.StartBatch("curl.exe", args, start, count);
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

    // Called by AppLauncherStep2 (on main thread)
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string reason)
    {
        if (_isSwitchingSlice) return;

        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            Debug.LogWarning($"[AdvanceBatch] FAILED start={batchStart} count={batchCount} exit={exitCode} reason={reason}");

            // On failure, revert batch indices so they can be retried
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            return;
        }

        int marked = 0;
        for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
        {
            // Only count those that were still marked Downloading
            if (filesReadinessStatus[i] == readiness.Downloading)
            {
                filesReadinessStatus[i] = readiness.Downloaded;
                marked++;
            }
            else
            {
                // If state drifted, still mark as Downloaded to keep system moving (Step 2 minimal)
                filesReadinessStatus[i] = readiness.Downloaded;
                marked++;
            }
        }

        downloadedCount += marked;
        // Debug.Log($"[AdvanceBatch] OK start={batchStart} count={batchCount} marked={marked} downloadedCount={downloadedCount} activeBatches={activeBatches}");
    }

    // =========================================================
    // Decode (unchanged from Step 1; still FIFO)
    // =========================================================
    //private async void ReadSingleMeshFromFile(string fileName, int position)
    //{
    //    byte[] stream = ReadStreamFromDownloadedFile(fileName);

    //    if (stream != null)
    //    {
    //        var meshDataArray = Mesh.AllocateWritableMeshData(1);
    //        await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

    //        Mesh tempMesh = new Mesh();
    //        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

    //        // Keep enqueue roughly in order (same as before)
    //        if (position != 0)
    //        {
    //            while (filesReadinessStatus[position - 1] == readiness.Downloaded)
    //                await Task.Delay(1);
    //        }
    //        else
    //        {
    //            while (filesReadinessStatus[numberOfFiles - 1] == readiness.Downloaded)
    //                await Task.Delay(1);
    //        }

    //        loadedMeshes.Enqueue(tempMesh);
    //    }
    //    else
    //    {
    //        await Task.Delay(1);
    //    }

    //    downloadedCount -= 1;
    //    filesReadinessStatus[position] = readiness.Loaded;
    //}

    private async void ReadSingleMeshFromFile(string fileName, int position)
    {
        int mySession = _sessionId;

        byte[] stream = ReadStreamFromDownloadedFile(fileName);
        if (stream == null)
        {
            await Task.Delay(1);
            return;
        }

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

        // Se trocou slice enquanto eu estava decodificando, descarta o resultado
        if (mySession != _sessionId || _isSwitchingSlice)
        {
            // Não mexe em contadores/estados, não enfileira mesh
            await Task.Delay(1);
            return;
        }

        Mesh tempMesh = new Mesh();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

        // Gate de ordem (igual)
        if (position != 0)
        {
            while (filesReadinessStatus[position - 1] == readiness.Downloaded)
            {
                if (mySession != _sessionId || _isSwitchingSlice) { Destroy(tempMesh); return; }
                await Task.Delay(1);
            }
        }
        else
        {
            while (filesReadinessStatus[numberOfFiles - 1] == readiness.Downloaded)
            {
                if (mySession != _sessionId || _isSwitchingSlice) { Destroy(tempMesh); return; }
                await Task.Delay(1);
            }
        }

        loadedMeshes.Enqueue(tempMesh);

        downloadedCount -= 1;
        filesReadinessStatus[position] = readiness.Loaded;
    }

    private byte[] ReadStreamFromDownloadedFile(string fileName)
    {
        string dir = Path.Combine(Application.persistentDataPath, "Downloads");
        string full = Path.Combine(dir, fileName);

        if (!File.Exists(full))
            return null;

        try { return File.ReadAllBytes(full); }
        catch (Exception ex)
        {
            Debug.LogError($"[DracoMultiCurlStep2] Error reading file {full}: {ex.Message}");
            return null;
        }
    }

    // =========================================================
    // Play (unchanged)
    // =========================================================
    private async void PlaySingleFile()
    {
        particlesScript.Set(currentMesh);

        float elapsedS = Time.realtimeSinceStartup - startTime;
        float elapsedMS = elapsedS * 1000f;

        if (elapsedMS < inverseFPSms)
        {
            int delay = Mathf.Max(1, (int)Math.Round(inverseFPSms - elapsedMS));
            await Task.Delay(delay);
        }
        else
        {
            await Task.Delay(1);
        }

        startTime = Time.realtimeSinceStartup;
        playerReady = true;
    }

    // =========================================================
    // Public controls (UI)
    // =========================================================
    public void SetNewIP(string newIP) { HostPath = newIP; ResetHostPath(); }
    public void SetNewPort(string newPort) { _port = newPort; ResetHostPath(); }

    public void SetPortFromSliceList(int slice)
    {
        currentSlice = slice;
        _port = sliceAddressList[currentSlice].ToString();
        ResetHostPath();
    }

    public void SetQualityFromQualityList(int quality)
    {
        currentFiles = Mathf.Clamp(quality, 0, _files.Length - 1);
        ResetHostPath();
    }

    public void Reconnect()
    {
        UpdateDracoFiles();
        activeBatches = 0;
        nextDownloadIndex = 0;
        downloadedCount = 0;
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (int.TryParse(newFramerate, out int outFps))
        {
            FPS = outFps;
            inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        }
    }

    public void SwitchSliceHard(int slice)
    {
        _isSwitchingSlice = true;
        _sessionId++; // invalida tasks async antigas (decode)

        // 1) Mata todos os curls em andamento
        appLauncher?.KillAllProcesses();

        // 2) Reseta scheduler/contadores
        activeBatches = 0;
        nextDownloadIndex = 0;
        downloadedCount = 0;

        // 3) Reseta ponteiros
        currentLoadedNumber = 0;
        currentPlayingNumber = 0;
        playerReady = true;

        // 4) Limpa fila de meshes (evita misturar slice antigo com novo)
        if (loadedMeshes != null)
        {
            while (loadedMeshes.Count > 0)
            {
                var m = loadedMeshes.Dequeue();
                if (m != null) Destroy(m);
            }
        }

        // 5) Limpa currentMesh
        if (currentMesh != null) DestroyImmediate(currentMesh);
        currentMesh = new Mesh();

        // 6) Reseta estados por frame
        if (filesReadinessStatus != null)
        {
            for (int i = 0; i < filesReadinessStatus.Length; i++)
                filesReadinessStatus[i] = readiness.None;
        }

        // 7) Troca slice/porta e atualiza URL base
        SetPortFromSliceList(slice);

        // 8) Garante lista de arquivos configurada (sem depender do OnEnable)
        if (dracoFiles == null || dracoFiles.Length != numberOfFiles)
            UpdateDracoFiles();

        // 9) Reinicia temporização do player
        startTime = Time.realtimeSinceStartup;

        _isSwitchingSlice = false;
    }

    // =========================================================
    // Main loop (STEP 2: multi-batch download + same decode/play)
    // =========================================================
    private void Update()
    {
        if (_isSwitchingSlice) return;
        if (dracoFiles == null) return;

        // 1) Download (multi-curl scheduling)
        TryStartDownloadBatches();

        // 2) Decode (same gating as Step 1)
        if (loadedMeshes.Count < loadedFilesMaxSize &&
            filesReadinessStatus[currentLoadedNumber] == readiness.Downloaded)
        {
            ReadSingleMeshFromFile(dracoFiles[currentLoadedNumber], currentLoadedNumber);
            currentLoadedNumber = (currentLoadedNumber < numberOfFiles - 1) ? currentLoadedNumber + 1 : 0;
        }

        // 3) Play (same)
        if (playerReady &&
            filesReadinessStatus[currentPlayingNumber] == readiness.Loaded)
        {
            playerReady = false;
            filesReadinessStatus[currentPlayingNumber] = readiness.None;
            currentPlayingNumber = (currentPlayingNumber < numberOfFiles - 1) ? currentPlayingNumber + 1 : 0;

            if (currentMesh != null) DestroyImmediate(currentMesh);
            loadedMeshes.TryDequeue(out currentMesh);

            PlaySingleFile();
        }
    }
}