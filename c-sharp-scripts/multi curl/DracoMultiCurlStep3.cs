using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoMultiCurlStep3 : MonoBehaviour
{
    // =========================================================
    // Server / URL configuration
    // =========================================================
    [Header("Server")]
    public string HostPath;
    [SerializeField] private string[] _files;
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

    [Header("Buffering")]
    public int batchSize = 30;
    [SerializeField] private int numberOfFiles = 300;
    private int loadedFilesMaxSize = 90;

    // =========================================================
    // Parallel Download (Step 3 keeps Step 2 scheduler)
    // =========================================================
    [Header("Parallel Download")]
    public int maxParallelBatches = 2;
    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public AppLauncherStep2 appLauncher;
    public DracoToParticles particlesScript;

    // =========================================================
    // Internal state
    // =========================================================
    private string[] dracoFiles;

    // STEP 3: index-based decoded buffer (no FIFO queue)
    private Mesh[] decodedMeshes;

    private Mesh currentMesh;
    private float startTime = 0f;

    private int currentLoadedNumber = 0;   // decode scan pointer
    private int currentPlayingNumber = 0;  // play pointer (strict order)

    private bool playerReady = true;

    private int downloadedCount = 0; // counts Downloaded items
    private int decodedCount = 0;    // counts Loaded items (meshes ready)

    // STEP 3: explicit state machine incl. Decoding
    private enum readiness { None, Downloading, Downloaded, Decoding, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    // Watchdog (optional but recommended)
    [Header("Watchdog")]
    public float stallSecondsToSkip = 2.0f;
    private int _lastPlayIndex = -1;
    private float _stallStart = 0f;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void OnEnable()
    {
        if (_files == null || _files.Length == 0 || appLauncher == null || particlesScript == null)
        {
            Debug.LogError("[DracoMultiCurlStep3] Missing references/config.");
            enabled = false;
            return;
        }

        currentMesh = new Mesh();
        inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        ResetHostPath();
        InitOrResetState();
    }

    private void InitOrResetState()
    {
        // file list
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        // state arrays
        filesReadinessStatus = new readiness[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            filesReadinessStatus[i] = readiness.None;

        // decoded mesh slots
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

        // pointers/counters
        currentLoadedNumber = 0;
        currentPlayingNumber = 0;
        nextDownloadIndex = 0;

        activeBatches = 0;
        downloadedCount = 0;
        decodedCount = 0;

        playerReady = true;
        _lastPlayIndex = -1;
        _stallStart = Time.realtimeSinceStartup;
    }

    private void ResetHostPath()
    {
        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);
        fullPath = $"{HttpPrefix}{HostPath}:{_port}/{_files[currentFiles]}";
        if (!fullPath.EndsWith("/")) fullPath += "/";
    }

    // =========================================================
    // Download scheduling (same idea as Step 2)
    // =========================================================
    private void TryStartDownloadBatches()
    {
        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return;

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            string args = "--http3 --parallel";
            for (int i = start; i < end; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;
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

    // called by launcher (main thread)
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string reason)
    {
        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            // failure => allow retry
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            Debug.LogWarning($"[AdvanceBatch] FAILED start={batchStart} count={batchCount} exit={exitCode} reason={reason}");
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
            else if (filesReadinessStatus[i] == readiness.None)
            {
                // tolerate drift
                filesReadinessStatus[i] = readiness.Downloaded;
                marked++;
            }
        }
        downloadedCount += marked;
    }

    // =========================================================
    // Decode (Step 3): no FIFO, no "gate". Uses Decoding state.
    // =========================================================
    private void TryDecodeOne()
    {
        if (decodedCount >= loadedFilesMaxSize) return; // cap loaded meshes

        // scan for a Downloaded item starting from currentLoadedNumber
        int found = FindStateFrom(currentLoadedNumber, readiness.Downloaded);
        if (found < 0) return;

        filesReadinessStatus[found] = readiness.Decoding;
        downloadedCount = Mathf.Max(0, downloadedCount - 1);

        currentLoadedNumber = (found + 1) % numberOfFiles;
        DecodeMeshAtIndex(found);
    }

    private int FindStateFrom(int startFrom, readiness target)
    {
        for (int k = 0; k < numberOfFiles; k++)
        {
            int i = (startFrom + k) % numberOfFiles;
            if (filesReadinessStatus[i] == target)
                return i;
        }
        return -1;
    }

    private async void DecodeMeshAtIndex(int index)
    {
        string fileName = dracoFiles[index];
        string dir = Path.Combine(Application.persistentDataPath, "Downloads");
        string path = Path.Combine(dir, fileName);

        byte[] stream = null;
        try
        {
            if (File.Exists(path))
                stream = File.ReadAllBytes(path);
        }
        catch
        {
            stream = null;
        }

        if (stream == null)
        {
            // file not ready => return to Downloaded so we can retry later
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
            Debug.LogError($"[Decode] FAIL idx={index} {ex.Message}");
            filesReadinessStatus[index] = readiness.None; // allow re-download
        }
    }

    // =========================================================
    // Play (Step 3): consume by index. Add watchdog for holes.
    // =========================================================
    private void TryPlayOne()
    {
        if (!playerReady) return;

        int i = currentPlayingNumber;

        if (filesReadinessStatus[i] != readiness.Loaded || decodedMeshes[i] == null)
            return;

        playerReady = false;

        if (currentMesh != null) DestroyImmediate(currentMesh);
        currentMesh = decodedMeshes[i];
        decodedMeshes[i] = null;

        filesReadinessStatus[i] = readiness.None;
        decodedCount = Mathf.Max(0, decodedCount - 1);

        currentPlayingNumber = (i + 1) % numberOfFiles;

        PlaySingleFile();
    }

    private async void PlaySingleFile()
    {
        particlesScript.Set(currentMesh);

        float elapsedMS = (Time.realtimeSinceStartup - startTime) * 1000f;
        if (elapsedMS < inverseFPSms)
            await Task.Delay(Mathf.Max(1, (int)Math.Round(inverseFPSms - elapsedMS)));
        else
            await Task.Delay(1);

        startTime = Time.realtimeSinceStartup;
        playerReady = true;
    }

    private void WatchdogSkipIfStuck()
    {
        if (_lastPlayIndex != currentPlayingNumber)
        {
            _lastPlayIndex = currentPlayingNumber;
            _stallStart = Time.realtimeSinceStartup;
            return;
        }

        if (Time.realtimeSinceStartup - _stallStart < stallSecondsToSkip)
            return;

        // find next Loaded
        int nextLoaded = FindStateFrom((currentPlayingNumber + 1) % numberOfFiles, readiness.Loaded);
        if (nextLoaded >= 0 && decodedMeshes[nextLoaded] != null)
        {
            Debug.LogWarning($"[WATCHDOG] stuck at {currentPlayingNumber} -> skip to {nextLoaded}");
            currentPlayingNumber = nextLoaded;
            _stallStart = Time.realtimeSinceStartup;
        }
    }

    // =========================================================
    // Public controls
    // =========================================================
    public void SetPortFromSliceList(int slice)
    {
        currentSlice = slice;
        _port = sliceAddressList[currentSlice].ToString();
        ResetHostPath();
        // optional: InitOrResetState(); if you want hard reset on slice change
    }

    // =========================================================
    // Main loop
    // =========================================================
    private void Update()
    {
        if (dracoFiles == null) return;

        TryStartDownloadBatches();
        TryDecodeOne();
        TryPlayOne();
        //WatchdogSkipIfStuck();
    }
}