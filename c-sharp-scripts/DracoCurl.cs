using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoCurl : MonoBehaviour
{
    [Header("Server")]
    public string HostPath = "ateixs.me";
    private const string _http = "https://";
    private string _port = "443";
    private string fullPath;

    [Header("Content paths")]
    [SerializeField] private string[] _files;
    [SerializeField] private int currentFiles = 0;

    [Header("Slices / Ports")]
    [SerializeField] private int[] sliceAddressList;
    private int currentSlice = 0;

    [Header("Playback")]
    public float FPS = 30f;
    private float inverseFPS;

    public int batchSize = 30;
    private int loadedFilesMaxSize = 90;

    [Header("Parallel Download")]
    public int maxParallelBatches = 3;
    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    [Header("References")]
    public AppLauncher appLauncher;
    public DracoToParticles particlesScript;

    [Header("Sequence config")]
    [SerializeField] private int numberOfFiles = 300;

    private string[] dracoFiles;
    private Mesh[] decodedMeshes;

    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = 0;
    private Mesh currentMesh;
    private float startTime = 0f;

    private int decodedCount = 0;
    private int downloadedCount = 0;
    private bool playerReady = true;

    private enum readiness { None, Downloading, Downloaded, Decoding, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    // -------------------- Debug / Watchdogs --------------------
    [Header("Debug / Watchdog")]
    public float debugInterval = 1.0f;
    public float stallSecondsToSkip = 2.0f;      // se travar no playIndex por > X s, pula
    public bool verbosePlayBlockLogs = false;

    private float _debugLastPrint = 0f;
    private float _lastProgressTime = 0f;
    private int _lastPlayIndex = -1;
    private float _playIndexStallStart = 0f;

    private void Awake()
    {
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoCurl] _files vazio. Configure pelo menos um path (ex: \"draco/\").");
            enabled = false;
            return;
        }

        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);

        if (string.IsNullOrEmpty(HostPath))
            HostPath = "localhost";

        ResetHostPath();
    }

    private void OnEnable()
    {
        inverseFPS = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;
        _lastProgressTime = Time.realtimeSinceStartup;

        currentMesh = new Mesh();
        UpdateDracoFiles();
    }

    private void OnDisable()
    {
        appLauncher?.KillAllProcesses();
    }

    private void UpdateDracoFiles()
    {
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        filesReadinessStatus = new readiness[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            filesReadinessStatus[i] = readiness.None;

        if (decodedMeshes != null)
        {
            for (int i = 0; i < decodedMeshes.Length; i++)
            {
                if (decodedMeshes[i] != null) Destroy(decodedMeshes[i]);
                decodedMeshes[i] = null;
            }
        }
        decodedMeshes = new Mesh[numberOfFiles];

        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
        downloadedCount = 0;
        decodedCount = 0;

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        activeBatches = 0;
        nextDownloadIndex = 0;

        playerReady = true;

        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        Debug.Log($"[DracoCurl] Init fullPath={fullPath} numberOfFiles={numberOfFiles} loadedFilesMaxSize={loadedFilesMaxSize}");
    }

    // =========================================================
    // DOWNLOAD
    // =========================================================
    private void TryStartDownloadBatches()
    {
        if (dracoFiles == null) return;
        if (string.IsNullOrEmpty(fullPath))
        {
            Debug.LogError("[DracoCurl] fullPath vazio. Verifique HostPath/_files.");
            return;
        }

        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return;

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            // DICA: args mais robustos
            // --fail: não cria arquivo 0 bytes em HTTP error (e força exit code != 0)
            // --connect-timeout / --max-time: evita curl preso
            // --retry: tenta de novo em falhas transitórias
            string appArgs = "--http3 --parallel --fail --connect-timeout 5 --max-time 30 --retry 2 --retry-delay 0";

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

    // Recebe agora tag+reason para explicar exitCode=-1
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string tag, string reason)
    {
        Debug.Log($"[AdvanceBatch] tag={tag} start={batchStart} count={batchCount} exitCode={exitCode} reason={reason} activeBatches(before)={activeBatches}");

        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            Debug.LogWarning($"[DracoCurl] Batch {batchStart}-{batchStart + batchCount - 1} FAILED (exitCode={exitCode}) reason={reason}");
            TouchProgress();
            return;
        }

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
        TouchProgress();
    }

    // =========================================================
    // DECODE
    // =========================================================
    private void TryDecode()
    {
        if (decodedCount >= loadedFilesMaxSize) return;

        // 1) tentativa rápida
        int scanLimit = Mathf.Min(numberOfFiles, batchSize * 2);
        int foundIdx = FindDownloadedInWindow(currentLoadedNumber, scanLimit);

        // 2) se não achou mas existe Downloaded no sistema, faz full scan
        if (foundIdx < 0)
        {
            int totalDownloaded = CountState(readiness.Downloaded);
            if (totalDownloaded > 0)
            {
                foundIdx = FindDownloadedInWindow(currentLoadedNumber, numberOfFiles);
            }
        }

        if (foundIdx < 0) return;

        filesReadinessStatus[foundIdx] = readiness.Decoding;
        downloadedCount = Mathf.Max(0, downloadedCount - 1);

        currentLoadedNumber = (foundIdx + 1) % numberOfFiles;
        DecodeMeshAtIndex(foundIdx);
    }

    private int FindDownloadedInWindow(int startIndex, int window)
    {
        int limit = Mathf.Min(window, numberOfFiles);
        for (int k = 0; k < limit; k++)
        {
            int i = (startIndex + k) % numberOfFiles;
            if (filesReadinessStatus[i] == readiness.Downloaded)
                return i;
        }
        return -1;
    }

    private async void DecodeMeshAtIndex(int index)
    {
        string fileName = dracoFiles[index];
        string dir = Path.Combine(Application.persistentDataPath, "Downloads");
        byte[] stream = ReadStreamFromDownloadedFile(fileName, dir);

        if (stream == null)
        {
            Debug.LogWarning($"[Decode] stream null idx={index} file={fileName} -> volta Downloaded");
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

            Debug.Log($"[Decode] OK idx={index} file={fileName} vtx={tempMesh.vertexCount}");
            TouchProgress();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Decode] FAIL idx={index} file={fileName}: {ex.Message}");
            filesReadinessStatus[index] = readiness.None;
            TouchProgress();
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
        if (currentMesh == null || particlesScript == null)
        {
            Debug.LogWarning("[PlaySingleFile] currentMesh or particlesScript is null.");
            playerReady = true;
            return;
        }

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

        if (filesReadinessStatus[i] != readiness.Loaded)
        {
            if (verbosePlayBlockLogs)
                Debug.Log($"[PLAY-BLOCK] idx={i} state={filesReadinessStatus[i]} LoadedTotal={CountState(readiness.Loaded)} DownloadedTotal={CountState(readiness.Downloaded)}");
            return;
        }

        if (decodedMeshes[i] == null)
        {
            Debug.LogWarning($"[PLAY-BLOCK] idx={i} está Loaded mas decodedMeshes[idx] é null");
            return;
        }

        playerReady = false;

        if (currentMesh != null) Destroy(currentMesh);
        currentMesh = decodedMeshes[i];
        decodedMeshes[i] = null;

        decodedCount = Mathf.Max(0, decodedCount - 1);
        filesReadinessStatus[i] = readiness.None;

        currentPlayingNumber = (i + 1) % numberOfFiles;

        Debug.Log($"[Play] frame={i} OK next={currentPlayingNumber}");
        TouchProgress();

        PlaySingleFile();
    }

    // =========================================================
    // Watchdogs
    // =========================================================
    private void TouchProgress()
    {
        _lastProgressTime = Time.realtimeSinceStartup;
    }

    private int CountState(readiness r)
    {
        int c = 0;
        for (int i = 0; i < filesReadinessStatus.Length; i++)
            if (filesReadinessStatus[i] == r) c++;
        return c;
    }

    private int FindNextLoadedIndex(int startFrom)
    {
        for (int k = 0; k < numberOfFiles; k++)
        {
            int i = (startFrom + k) % numberOfFiles;
            if (filesReadinessStatus[i] == readiness.Loaded && decodedMeshes[i] != null)
                return i;
        }
        return -1;
    }

    private void WatchdogStallAndSkipIfNeeded()
    {
        // detecta stall do playIndex
        if (_lastPlayIndex != currentPlayingNumber)
        {
            _lastPlayIndex = currentPlayingNumber;
            _playIndexStallStart = Time.realtimeSinceStartup;
            return;
        }

        // playIndex não mudou
        if (Time.realtimeSinceStartup - _playIndexStallStart < stallSecondsToSkip)
            return;

        // Se estamos travados, tenta pular para o próximo Loaded
        int nextLoaded = FindNextLoadedIndex(currentPlayingNumber + 1);
        if (nextLoaded >= 0)
        {
            Debug.LogWarning($"[WATCHDOG] playIndex stuck at {currentPlayingNumber}. Skipping to next Loaded={nextLoaded}");
            currentPlayingNumber = nextLoaded;
            _playIndexStallStart = Time.realtimeSinceStartup;
            TouchProgress();
        }
        else
        {
            // Não há Loaded disponível: talvez download/decode travaram
            Debug.LogWarning($"[WATCHDOG] playIndex stuck at {currentPlayingNumber} but no Loaded frames available. Downloaded={CountState(readiness.Downloaded)} activeBatches={activeBatches}");
        }
    }

    // =========================================================
    // Public controls
    // =========================================================
    public void SetNewIP(string newIP) { HostPath = newIP; ResetHostPath(); }
    public void SetNewPort(string newPort) { _port = newPort; ResetHostPath(); }

    public void SetPortFromSliceList(int slice)
    {
        if (sliceAddressList == null || sliceAddressList.Length == 0) return;
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
            Debug.LogError("[DracoCurl] _files vazio ao ResetHostPath.");
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

        WatchdogStallAndSkipIfNeeded();

        // DEBUG: snapshot de estado
        if (Time.time - _debugLastPrint > debugInterval)
        {
            _debugLastPrint = Time.time;
            Debug.Log($"[STATE] activeBatches={activeBatches} downloadedCount={downloadedCount} decodedCount={decodedCount} playIndex={currentPlayingNumber} " +
                      $"None={CountState(readiness.None)} Down={CountState(readiness.Downloading)} Dled={CountState(readiness.Downloaded)} Dec={CountState(readiness.Decoding)} Loaded={CountState(readiness.Loaded)}");
        }
    }
}