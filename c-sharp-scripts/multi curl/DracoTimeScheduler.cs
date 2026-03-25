using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Draco;

public class DracoTimeScheduler : MonoBehaviour
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
    // Parallel Download + Scheduler (Time + Backpressure)
    // =========================================================
    [Header("Parallel Download")]
    [Tooltip("Set to 1 to behave closer to single-batch. Increase to 2/3 after validating.")]
    public int maxParallelBatches = 2;

    [Header("Time Scheduler")]
    [Tooltip("Scheduler interval multiplier. Default 0.5 matches: (batchSize/FPS)/2")]
    [Range(0.1f, 1.0f)] public float scheduleIntervalMultiplier = 0.5f;

    [Tooltip("Minimum scheduler interval in seconds (avoid hammering the scheduler).")]
    public float minScheduleIntervalSec = 0.05f;

    [Tooltip("Optional: more aggressive scheduling during startup (seconds). 0 disables.")]
    public float startupBurstDurationSec = 1.0f;

    [Tooltip("During startup burst, scheduler interval is clamped to this value (seconds).")]
    public float startupBurstIntervalSec = 0.02f;

    [Tooltip("Max number of frames allowed in Downloading state (in-flight). Default = 2*batchSize.")]
    public int targetInFlightOverride = 0; // 0 = auto (2*batchSize)

    private int activeBatches = 0;
    private int nextDownloadIndex = 0;

    private float nextScheduleTime = 0f;
    private float scheduleIntervalSec = 0.1f;
    private int targetReady = 0;
    private int targetInFlight = 0;

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

    private enum readiness { None, Downloading, Downloaded, Loaded }
    [SerializeField] private readiness[] filesReadinessStatus;

    // =========================================================
    // Unity lifecycle
    // =========================================================
    private void OnEnable()
    {
        if (_files == null || _files.Length == 0)
        {
            Debug.LogError("[DracoTimeSchedule] _files is empty.");
            enabled = false;
            return;
        }
        if (appLauncher == null)
        {
            Debug.LogError("[DracoTimeSchedule] appLauncher reference is missing.");
            enabled = false;
            return;
        }
        if (particlesScript == null)
        {
            Debug.LogError("[DracoTimeSchedule] particlesScript reference is missing.");
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
        RecomputeSchedulerParams();

        currentLoadedNumber = 0;
        currentPlayingNumber = 0;

        activeBatches = 0;
        nextDownloadIndex = 0;

        playerReady = true;
        nextScheduleTime = 0f; // allow immediate scheduling on start
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

    private void RecomputeSchedulerParams()
    {
        // scheduleInterval ≈ (batchSize/FPS)/2  (seconds)
        float baseInterval = (batchSize / Mathf.Max(1f, FPS)) * scheduleIntervalMultiplier;
        scheduleIntervalSec = Mathf.Max(minScheduleIntervalSec, baseInterval);

        targetReady = loadedFilesMaxSize; // as requested

        int autoInFlight = 2 * Mathf.Max(1, batchSize);
        targetInFlight = (targetInFlightOverride > 0) ? targetInFlightOverride : autoInFlight;
    }

    // =========================================================
    // Scheduler helpers (Option B)
    // =========================================================
    private int Count(readiness state)
    {
        int c = 0;
        for (int i = 0; i < filesReadinessStatus.Length; i++)
            if (filesReadinessStatus[i] == state) c++;
        return c;
    }

    // For the current Step2-style FIFO queue, the most honest "ready" metric is just loadedMeshes.Count,
    // because that's what playback consumes.
    private int ReadyBuffer() => loadedMeshes.Count;

    private int InFlight() => Count(readiness.Downloading);

    private int DownloadedBuffer() => Count(readiness.Downloaded);

    private bool BufferLow() => ReadyBuffer() < (targetReady / 2);

    private int FindNextNoneFromPlayhead(int playhead, int window)
    {
        int w = Mathf.Clamp(window, 1, numberOfFiles);
        for (int k = 0; k < w; k++)
        {
            int i = (playhead + k) % numberOfFiles;
            if (filesReadinessStatus[i] == readiness.None)
                return i;
        }
        return -1;
    }

    private int ContiguousNoneFrom(int start, int maxCount)
    {
        if (start < 0) return 0;
        int end = Mathf.Min(start + maxCount, numberOfFiles); // no wrap in this "contiguous" helper
        int count = 0;
        for (int i = start; i < end; i++)
        {
            if (filesReadinessStatus[i] != readiness.None) break;
            count++;
        }
        return count;
    }

    // =========================================================
    // Download scheduling (Option B: time + backpressure)
    // =========================================================
    private void SchedulerTick()
    {
        if (dracoFiles == null) return;

        float now = Time.realtimeSinceStartup;

        // Optional: be more aggressive in the first seconds after enable/reconnect
        float effectiveInterval = scheduleIntervalSec;
        if (startupBurstDurationSec > 0f && (now - startTime) < startupBurstDurationSec)
            effectiveInterval = Mathf.Min(effectiveInterval, startupBurstIntervalSec);

        // Gate: only schedule if time tick reached OR buffer is low
        if (now < nextScheduleTime && !BufferLow())
            return;

        nextScheduleTime = now + effectiveInterval;

        // Backpressure uses counts that include Downloading
        while (activeBatches < maxParallelBatches
            && ReadyBuffer() < targetReady
            && (DownloadedBuffer() + InFlight()) < loadedFilesMaxSize
            && InFlight() < targetInFlight)
        {
            // Prefer scheduling near playhead to avoid "holes" that block decode/play
            int start = FindNextNoneFromPlayhead(currentPlayingNumber, window: loadedFilesMaxSize);
            if (start < 0)
            {
                // Fallback: global search using nextDownloadIndex
                start = FindNextNoneFromPlayhead(nextDownloadIndex, window: numberOfFiles);
                if (start < 0) break;
            }

            int count = ContiguousNoneFrom(start, batchSize);
            if (count <= 0)
            {
                nextDownloadIndex = (start + 1) % numberOfFiles;
                break;
            }

            // Mark as Downloading
            for (int i = start; i < start + count; i++)
                filesReadinessStatus[i] = readiness.Downloading;

            // Build curl args for this batch
            string args = "--http3 --parallel";
            for (int i = start; i < start + count; i++)
            {
                string filename = dracoFiles[i];
                string outPath = Path.Combine(Application.persistentDataPath, "Downloads", filename);
                string url = fullPath + filename;
                args += $" -o \"{outPath}\" \"{url}\"";
            }

            activeBatches++;
            nextDownloadIndex = (start + count < numberOfFiles) ? (start + count) : 0;

            appLauncher.StartBatch("curl.exe", args, start, count);
        }
    }

    // Called by AppLauncherStep2 (on main thread)
    public void AdvanceBatch(int batchStart, int batchCount, int exitCode, string reason)
    {
        activeBatches = Mathf.Max(0, activeBatches - 1);

        if (exitCode != 0)
        {
            Debug.LogWarning($"[AdvanceBatch] FAILED start={batchStart} count={batchCount} exit={exitCode} reason={reason}");

            // revert to None so scheduler can retry
            for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
                filesReadinessStatus[i] = readiness.None;

            return;
        }

        for (int i = batchStart; i < batchStart + batchCount && i < numberOfFiles; i++)
        {
            // Normal path: Downloading -> Downloaded
            if (filesReadinessStatus[i] == readiness.Downloading)
                filesReadinessStatus[i] = readiness.Downloaded;
            else
                filesReadinessStatus[i] = readiness.Downloaded; // keep moving even if state drifted
        }
    }

    // =========================================================
    // Decode (kept close to Step2: FIFO queue + gating)
    // =========================================================
    private async void ReadSingleMeshFromFile(string fileName, int position)
    {
        byte[] stream = ReadStreamFromDownloadedFile(fileName);
        if (stream == null)
        {
            await Task.Delay(1);
            return;
        }

        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

        Mesh tempMesh = new Mesh();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

        // Keep enqueue roughly in order (same as Step2)
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
            Debug.LogError($"[DracoTimeSchedule] Error reading file {full}: {ex.Message}");
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
        // “soft” reconnect: keep it similar to Step2 behavior
        UpdateDracoFiles();
        activeBatches = 0;
        nextDownloadIndex = 0;

        // reset scheduler timing so it schedules immediately again
        nextScheduleTime = 0f;
        startTime = Time.realtimeSinceStartup;

        RecomputeSchedulerParams();
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (int.TryParse(newFramerate, out int outFps))
        {
            FPS = outFps;
            inverseFPSms = 1000f / Mathf.Max(1f, FPS);

            // scheduler depends on FPS, so recompute
            RecomputeSchedulerParams();
        }
    }

    // =========================================================
    // Main loop (Time scheduler + same decode/play)
    // =========================================================
    private void Update()
    {
        if (dracoFiles == null) return;

        // 1) Download scheduling: hybrid time + buffer low
        SchedulerTick();

        // 2) Decode: same gating as before
        if (loadedMeshes.Count < loadedFilesMaxSize &&
            filesReadinessStatus[currentLoadedNumber] == readiness.Downloaded)
        {
            ReadSingleMeshFromFile(dracoFiles[currentLoadedNumber], currentLoadedNumber);
            currentLoadedNumber = (currentLoadedNumber < numberOfFiles - 1) ? currentLoadedNumber + 1 : 0;
        }

        // 3) Play: same
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