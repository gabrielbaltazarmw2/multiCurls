using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoCurl : MonoBehaviour
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
    // Playback / buffering config
    // =========================================================
    [Header("Playback")]
    public float FPS = 30f;
    private float inverseFPSms;

    [Header("Buffering")]
    public int batchSize = 30;
    [SerializeField] private int numberOfFiles = 300;
    private int loadedFilesMaxSize = 90;

    // =========================================================
    // Multi-curl (first steps)
    // =========================================================
    [Header("Parallel Download (Multi-Curl)")]
    [Tooltip("Start with 1 to keep old behavior. Increase to 2/3 after validating.")]
    public int maxParallelBatches = 1;

    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    // =========================================================
    // References
    // =========================================================
    [Header("References")]
    public AppLauncher appLauncher;
    public DracoToParticles particlesScript;

    // =========================================================
    // Internal state
    // =========================================================
    private string[] dracoFiles;

    // PATCH: index-based decoded buffer (replaces Queue<Mesh>)
    private Mesh[] decodedMeshes;

    private Mesh currentMesh;
    private float startTime = 0f;

    private int currentLoadedNumber = 0;   // next index to decode
    private int currentPlayingNumber = 0;  // next index to play

    // Count of frames currently in Downloaded state (download backpressure)
    private int downloadedCount = 0;

    private bool playerReady = true;

    // Frame lifecycle:
    // None -> Downloading -> Downloaded -> Loaded -> None
    private enum readiness { None, Downloading, Downloaded, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void OnEnable()
    {
        currentMesh = new Mesh();

        inverseFPSms = 1000f / Mathf.Max(1f, FPS);
        startTime = Time.realtimeSinceStartup;

        Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, "Downloads"));

        ResetHostPath();
        UpdateDracoFiles();

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

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

        // PATCH: allocate/clear decoded mesh slots
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
    }

    private void ResetHostPath()
    {
        currentFiles = Mathf.Clamp(currentFiles, 0, _files.Length - 1);
        fullPath = $"{HttpPrefix}{HostPath}:{_port}/{_files[currentFiles]}";
        if (!fullPath.EndsWith("/")) fullPath += "/";
    }

    // =========================================================
    // DOWNLOAD
    // =========================================================
    private void TryStartDownloadBatches()
    {
        if (dracoFiles == null) return;
        if (string.IsNullOrEmpty(fullPath)) return;
        if (appLauncher == null) return;

        // Backpressure: don't overfill downloaded buffer
        while (activeBatches < maxParallelBatches && downloadedCount < loadedFilesMaxSize)
        {
            int start = FindNextNoneIndex(nextDownloadIndex);
            if (start < 0) return;

            int end = Mathf.Min(start + batchSize, numberOfFiles);
            int count = end - start;

            // mark as Downloading immediately
            for (int i = start; i < end; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            // Build curl args
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

    public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string reason)
    {
        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            Debug.LogWarning($"[AdvanceBatch] FAILED start={batchStart} count={batchCount} exit={exitCode} reason={reason}");
            return;
        }

        int marked = 0;
        for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
        {
            filesReadinessStatus[i] = readiness.Downloaded;
            marked++;
        }

        downloadedCount += marked;
    }

    // =========================================================
    // DECODE (PATCHED: store by index, do not enqueue)
    // =========================================================
    private void TryDecodeOne()
    {
        // Keep same backpressure spirit: only decode if we don't exceed buffer target.
        // Now we compute "how many Loaded we currently hold" by scanning a small window
        // would be expensive; instead keep simple: don't decode if too many Loaded already.
        // Minimal change: use CountLoaded() (O(N)) only if you want strict cap.
        // Here we keep it similar by limiting via decodedCount in later versions;
        // for first steps, we can simply decode when slot is empty and status is Downloaded.
        int i = currentLoadedNumber;

        if (filesReadinessStatus[i] == readiness.Downloaded && decodedMeshes[i] == null)
        {
            //filesReadinessStatus[i] = readiness.LoadingHackDecoding(); // NOTE: we do not have Decoding state in this first-step enum
            // ^ Can't do this because enum doesn't have Decoding. Keep Downloaded until success.
            // So: do not change state here.

            ReadSingleMeshFromFile(dracoFiles[i], i);

            currentLoadedNumber = (i < numberOfFiles - 1) ? i + 1 : 0;
        }
        else
        {
            // still advance pointer to avoid getting stuck if i is not decodable yet
            currentLoadedNumber = (i < numberOfFiles - 1) ? i + 1 : 0;
        }
    }

    private async void ReadSingleMeshFromFile(string fileName, int position)
    {
        byte[] stream = ReadStreamFromDownloadedFile(fileName, Path.Combine(Application.persistentDataPath, "Downloads"));

        if (stream != null)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

            Mesh tempMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

            // PATCH: store mesh by index (overwrites old if any)
            if (decodedMeshes[position] != null) Destroy(decodedMeshes[position]);
            decodedMeshes[position] = tempMesh;
            //loadedMeshes.Enqueue(tempMesh);

            downloadedCount = Mathf.Max(0, downloadedCount - 1);
            filesReadinessStatus[position] = readiness.Loaded;
        }
        else
        {
            // If file isn't ready, just yield and let Update try later.
            await Task.Delay(1);
        }
    }

    private byte[] ReadStreamFromDownloadedFile(string fileName, string dirPath)
    {
        string full = Path.Combine(dirPath, fileName);
        if (!File.Exists(full)) return null;

        try { return File.ReadAllBytes(full); }
        catch (Exception ex)
        {
            Debug.LogError($"[ReadStream] Error reading {full}: {ex.Message}");
            return null;
        }
    }

    // =========================================================
    // PLAY (PATCHED: fetch mesh by index instead of dequeue)
    // =========================================================
    private void TryPlayOne()
    {
        if (!playerReady) return;

        int i = currentPlayingNumber;

        if (filesReadinessStatus[i] != readiness.Loaded) return;
        if (decodedMeshes[i] == null) return; // safety

        playerReady = false;

        // consume the mesh for this exact frame index
        if (currentMesh != null) DestroyImmediate(currentMesh);
        currentMesh = decodedMeshes[i];
        decodedMeshes[i] = null;

        filesReadinessStatus[i] = readiness.None;

        currentPlayingNumber = (i < numberOfFiles - 1) ? i + 1 : 0;

        PlaySingleFile();
    }

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
    // Public controls (kept)
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

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;
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
    // Main loop
    // =========================================================
    private void Update()
    {
        if (dracoFiles == null) return;

        TryStartDownloadBatches();
        TryDecodeOne();
        TryPlayOne();
    }
}