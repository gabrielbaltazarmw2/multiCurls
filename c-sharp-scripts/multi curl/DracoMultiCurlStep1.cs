using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoMultiCurlStep1 : MonoBehaviour
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
    private string _port = "5002";
    private string fullPath; // e.g.: https://host:443/path/

    // =========================================================
    // Playback / Buffer
    // =========================================================
    [Header("Playback")]
    public float FPS = 30f;
    private float inverseFPSms; // milliseconds per frame
    public bool isLoop = true;  // kept for compatibility (not used in original logic)

    [Header("Buffering")]
    public int batchSize = 30;            // how many files per curl batch
    private int loadedFilesMaxSize = 90;  // derived from batchSize & numberOfFiles
    [SerializeField] private int numberOfFiles = 300;

    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public AppLauncherStep1 appLauncher;
    public DracoToParticles particlesScript;
    public AnimationFPSCounter counter; // optional (not used in original Update)

    // =========================================================
    // Internal state
    // =========================================================
    private string[] dracoFiles;          // "1000.drc" ... "1299.drc"
    private Queue<Mesh> loadedMeshes;     // decoded meshes in playback order
    private Mesh currentMesh;
    private float startTime = 0f;

    private int currentLoadedNumber = 0;  // next index to decode
    private int currentPlayingNumber = 0; // next index to play
    private int downloadingFilesFrom = 0; // start index for the next download batch

    private bool haltDownloading = false; // blocks starting another curl while one is running
    private bool playerReady = true;

    private int downloadedCount = 0;      // number of frames in "Downloaded" state

    // State machine per frame:
    // None -> (Downloaded by curl batch) -> (Loaded after decode) -> None after playback
    private enum readiness { None, Downloaded, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    // Legacy vars kept (present in original script)
    private int playIndex, currentPosition;
    public int PlayIndex { get => playIndex; set => playIndex = value; }

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void OnEnable()
    {
        // Basic validation (doesn't change pipeline logic, just avoids silent null issues)
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoCurl] _files is empty. Configure at least one content path.");
            enabled = false;
            return;
        }
        if (appLauncher == null)
        {
            Debug.LogError("[DracoCurl] appLauncher reference is missing.");
            enabled = false;
            return;
        }
        if (particlesScript == null)
        {
            Debug.LogError("[DracoCurl] particlesScript reference is missing.");
            enabled = false;
            return;
        }

        // Init runtime structures
        loadedMeshes = new Queue<Mesh>();
        currentMesh = new Mesh();

        PlayIndex = 0;
        currentPosition = -1;

        inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        // Prepare downloads folder
        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        // Build URL and file list
        //ResetHostPath();
        //UpdateDracoFiles();

        // Reset pipeline pointers
        downloadingFilesFrom = 0;
        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        downloadedCount = 0;
        haltDownloading = false;
        playerReady = true;
    }

    // =========================================================
    // Setup helpers
    // =========================================================
    private void UpdateDracoFiles()
    {
        // Build file name list: 1000.drc ... (1000 + numberOfFiles - 1).drc
        dracoFiles = new string[numberOfFiles];
        for (int i = 0; i < numberOfFiles; i++)
            dracoFiles[i] = (1000 + i) + ".drc";

        // IMPORTANT: keep original behavior:
        // only reset readiness array if size mismatch (do NOT wipe states on every reconnect)
        if (filesReadinessStatus == null || filesReadinessStatus.Length != numberOfFiles)
        {
            filesReadinessStatus = new readiness[numberOfFiles];
            for (int i = 0; i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;
        }

        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
    }

    private void ResetHostPath()
    {
        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);
        fullPath = $"{HttpPrefix}{HostPath}:{_port}/{_files[currentFiles]}";
        if (!fullPath.EndsWith("/")) fullPath += "/";
    }

    // // =========================================================
    // // Download control (single curl)
    // // =========================================================
    // /// <summary>
    // /// Called by AppLauncher when curl prints something to stdout (original behavior).
    // /// Marks the current batch as Downloaded and unblocks next download.
    // /// </summary>
    // public void AdvanceBatch()
    // {
    //     for (int i = downloadingFilesFrom; i < downloadingFilesFrom + batchSize && i < numberOfFiles; i++)
    //     {
    //         filesReadinessStatus[i] = readiness.Downloaded;
    //         downloadedCount += 1;
    //     }

    //     downloadingFilesFrom = (downloadingFilesFrom + batchSize < numberOfFiles) ? downloadingFilesFrom + batchSize : 0;
    //     haltDownloading = false;
    // }

    // =========================================================
// Download control (single curl) - STEP 1 (batch-aware)
// =========================================================
public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string reason)
{
    // STEP 1: keep behavior close to original.
    // (Later steps can add failure policy. For now, just log exitCode.)
    if (exitCode != 0)
        Debug.LogWarning($"[AdvanceBatch] exitCode={exitCode} reason={reason}");

    // Mark THIS batch range as Downloaded (batch-aware)
    for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
    {
        filesReadinessStatus[i] = readiness.Downloaded;
        downloadedCount += 1;
    }

    // Move pointer based on the batch that actually completed
    downloadingFilesFrom = (batchStart + batchCount < numberOfFiles) ? (batchStart + batchCount) : 0;

    // Unblock next download
    haltDownloading = false;

    Debug.Log($"[AdvanceBatch] start={batchStart} count={batchCount} exit={exitCode} downloadedCount={downloadedCount}");
}

    // =========================================================
    // Decode (original FIFO approach)
    // =========================================================
    private async void ReadSingleMeshFromFile(string fileName, int position)
    {
        byte[] stream = ReadStreamFromDownloadedFile(fileName);

        if (stream != null)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

            Mesh tempMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

            // Attempt to keep enqueue in order based on readiness of previous frame
            if (position != 0)
            {
                while (filesReadinessStatus[position - 1] == readiness.Downloaded)
                    await Task.Delay(1);
            }
            else
            {
                while (filesReadinessStatus[numberOfFiles - 1] == readiness.Downloaded)
                    await Task.Delay(1);
            }

            loadedMeshes.Enqueue(tempMesh);
        }
        else
        {
            // If file isn't present yet (or failed), retry later
            await Task.Delay(1);
        }

        // Kept in original behavior: decrement + mark Loaded here
        downloadedCount -= 1;
        filesReadinessStatus[position] = readiness.Loaded;
    }

    private byte[] ReadStreamFromDownloadedFile(string fileName)
    {
        string dir = Path.Combine(Application.persistentDataPath, "Downloads");
        string full = Path.Combine(dir, fileName);

        if (!File.Exists(full))
            return null;

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DracoCurl] Error reading file {full}: {ex.Message}");
            return null;
        }
    }

    // =========================================================
    // Play (FPS pacing)
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
        // Original code did not reset pointers aggressively here.
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (int.TryParse(newFramerate, out int outFps))
        {
            FPS = outFps;
            inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        }
    }

    // =========================================================
    // Main loop (single-batch download, in-order decode, in-order play)
    // =========================================================
    private void Update()
    {
        if (dracoFiles == null) return;

        // 1) Download: if buffer not full and not already downloading, start a curl batch
        if (downloadedCount < loadedFilesMaxSize &&
            !haltDownloading &&
            filesReadinessStatus[downloadingFilesFrom] == readiness.None)
        {
            haltDownloading = true;

            string appArgs = "--http3 --parallel";
            for (int i = downloadingFilesFrom; i < downloadingFilesFrom + batchSize && i < numberOfFiles; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;

                // Note: quoting paths is safer; keeping close to original behavior
                appArgs += $" -o {outPath} {url}";
            }

            int batchStart = downloadingFilesFrom;
            int batchCount = Mathf.Min(batchSize, numberOfFiles - batchStart); // (igual ao range do for)
            appLauncher.StartBatch("curl.exe", appArgs, batchStart, batchCount);
        }

        // 2) Decode: if buffer has room and next item is Downloaded, decode it
        if (loadedMeshes.Count < loadedFilesMaxSize &&
            filesReadinessStatus[currentLoadedNumber] == readiness.Downloaded)
        {
            ReadSingleMeshFromFile(dracoFiles[currentLoadedNumber], currentLoadedNumber);
            currentLoadedNumber = (currentLoadedNumber < numberOfFiles - 1) ? currentLoadedNumber + 1 : 0;
        }

        // 3) Play: if player ready and next item is Loaded, dequeue + play
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