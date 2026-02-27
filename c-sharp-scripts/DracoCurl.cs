using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Draco;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Streams Draco-compressed 3D frames from an HTTP server using multiple parallel curl
/// processes.  The pipeline has three stages running concurrently:
///   1. DOWNLOAD  – N curl slots each fetch a batch of files from the server.
///   2. DECODE    – Each downloaded file is decoded from Draco into a Unity Mesh.
///   3. PLAYBACK  – Decoded meshes are dequeued and rendered at the target FPS.
/// </summary>
public class DracoCurl : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════
    //  Inspector / Configuration
    // ═══════════════════════════════════════════════════════════════

    [Header("Server")]
    public string HostPath;
    [SerializeField] private string[] _qualityPaths;   // URL path per quality level
    [SerializeField] private int[]    _slicePorts;      // Port per slice/stream

    [Header("Playback")]
    public float FPS        = 30f;
    public bool  IsLoop     = true;
    [SerializeField] private int numberOfFiles = 300;

    [Header("Download – Parallelism")]
    [Tooltip("Number of curl processes running simultaneously.")]
    [SerializeField] private int parallelCurls = 3;

    [Tooltip("Number of files downloaded per curl invocation.")]
    [SerializeField] private int batchSize = 20;

    [Tooltip("Maximum decoded meshes kept in memory. Automatically clamped to a safe multiple of batchSize.")]
    [SerializeField] private int maxLoadedMeshes = 90;

    [Header("References")]
    public AppLauncher         appLauncher;
    public DracoToParticles    particlesScript;
    public AnimationFPSCounter counter;

    // ═══════════════════════════════════════════════════════════════
    //  Internal state
    // ═══════════════════════════════════════════════════════════════

    // File list and readiness tracking
    private string[]    dracoFiles;
    private FileState[] fileStates;        // per-file download/decode state

    // Download coordination
    private int         nextBatchStart;    // next file index to schedule for download
    private int[]       slotBatchStart;    // which file-batch each curl slot is handling
    private bool[]      slotActive;        // is this curl slot currently busy?
    private int         inFlightBatches;   // number of curl processes currently running

    // Decode coordination
    private int         nextDecodeIndex;   // next file index to decode
    private int         decodingInFlight;  // async decode tasks in progress
    private int         pendingDecodePosition; // ordering helper (mirrors old currentPosition)

    // Playback
    private Queue<Mesh> loadedMeshes;
    private Mesh        currentMesh;
    private bool        playerReady = true;
    private float       startTime;
    private float       inverseFPS;         // frame budget in milliseconds

    // Playback cursors
    private int         playbackIndex;      // next file index to mark as played

    // URL
    private string      fullPath;
    private string      _http = "https://";
    private string      _port = "443";
    private int         currentQuality;
    private int         currentSlice;

    // Stats
    private int         totalDownloaded;
    private int         totalDecoded;
    private int         totalPlayed;
    private float       lastStatsTime;

    // ═══════════════════════════════════════════════════════════════
    //  File state enum
    // ═══════════════════════════════════════════════════════════════

    private enum FileState
    {
        None,        // not yet scheduled
        Downloading, // curl slot has been assigned
        Downloaded,  // file exists on disk, waiting to be decoded
        Decoded      // mesh is in the queue, ready to play
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ═══════════════════════════════════════════════════════════════

    private void OnEnable()
    {
        Reset();
    }

    private void Update()
    {
        if (dracoFiles == null) return;

        LogStatsIfNeeded();
        TickDownload();
        TickDecode();
        TickPlayback();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Re-initializes file list and restarts the whole pipeline.</summary>
    public void Reconnect()
    {
        Reset();
        BuildFileList();
        Debug.Log($"[DracoCurl] Reconnected — {numberOfFiles} files, " +
                  $"{parallelCurls} parallel curls × {batchSize} files/batch.");
    }

    public void SetNewIP(string newIP)
    {
        HostPath = newIP;
        RebuildURL();
    }

    public void SetNewPort(string newPort)
    {
        _port = newPort;
        RebuildURL();
    }

    public void SetPortFromSliceList(int slice)
    {
        currentSlice = slice;
        _port = _slicePorts[currentSlice].ToString();
        RebuildURL();
    }

    public void SetQualityFromList(int quality)
    {
        currentQuality = quality;
        RebuildURL();
    }

    public void ChangeFramerate(string newFramerate)
    {
        if (int.TryParse(newFramerate, out int parsed))
        {
            FPS       = parsed;
            inverseFPS = 1000f / FPS;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Initialization helpers
    // ═══════════════════════════════════════════════════════════════

    private void Reset()
    {
        appLauncher.KillAll();

        loadedMeshes   = new Queue<Mesh>();
        currentMesh    = new Mesh();
        playerReady    = true;
        startTime      = 0f;
        inverseFPS     = 1000f / FPS;

        nextBatchStart    = 0;
        nextDecodeIndex   = 0;
        pendingDecodePosition = -1;
        playbackIndex     = 0;
        inFlightBatches   = 0;
        decodingInFlight  = 0;
        totalDownloaded   = 0;
        totalDecoded      = 0;
        totalPlayed       = 0;

        // Clamp maxLoadedMeshes to a sane value
        maxLoadedMeshes = Mathf.Max(maxLoadedMeshes, parallelCurls * batchSize * 2);

        // Per-slot tracking
        slotBatchStart = new int[parallelCurls];
        slotActive     = new bool[parallelCurls];
        for (int i = 0; i < parallelCurls; i++)
        {
            slotBatchStart[i] = -1;
            slotActive[i]     = false;
        }

        dracoFiles = null;
        fileStates = null;
    }

    private void BuildFileList()
    {
        dracoFiles = new string[numberOfFiles];
        fileStates = new FileState[numberOfFiles];

        for (int i = 0; i < numberOfFiles; i++)
        {
            dracoFiles[i] = (1000 + i) + ".drc";
            fileStates[i] = FileState.None;
        }

        Debug.Log($"[DracoCurl] File list built: {numberOfFiles} files.");
    }

    private void RebuildURL()
    {
        string qualityPath = (_qualityPaths != null && _qualityPaths.Length > currentQuality)
            ? _qualityPaths[currentQuality]
            : "";
        fullPath = _http + HostPath + ":" + _port + "/" + qualityPath;
        Debug.Log($"[DracoCurl] URL updated: {fullPath}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Download stage
    // ═══════════════════════════════════════════════════════════════

    private void TickDownload()
    {
        // Fill every free curl slot up to the parallelCurls limit
        for (int slot = 0; slot < parallelCurls; slot++)
        {
            if (slotActive[slot]) continue;
            if (loadedMeshes.Count >= maxLoadedMeshes) break;          // buffer full
            if (!FindNextDownloadableBatch(out int batchStart)) break;  // nothing to queue

            ScheduleDownload(slot, batchStart);
        }
    }

    /// <summary>
    /// Finds the next contiguous block of files all in FileState.None.
    /// Returns false if no such block exists right now.
    /// </summary>
    private bool FindNextDownloadableBatch(out int batchStart)
    {
        batchStart = -1;

        // Walk from nextBatchStart, with loop-around
        int searchFrom = nextBatchStart;
        for (int attempt = 0; attempt < numberOfFiles; attempt++)
        {
            int idx = (searchFrom + attempt) % numberOfFiles;
            if (fileStates[idx] == FileState.None)
            {
                batchStart       = idx;
                nextBatchStart   = (idx + batchSize) % numberOfFiles;
                return true;
            }
        }
        return false;
    }

    private void ScheduleDownload(int slot, int batchStart)
    {
        slotActive[slot]     = true;
        slotBatchStart[slot] = batchStart;
        inFlightBatches++;

        // Build curl argument string for the whole batch
        string appArgs = "--http3 --parallel";
        int    count   = 0;

        for (int i = 0; i < batchSize; i++)
        {
            int fileIdx = (batchStart + i) % numberOfFiles;
            if (fileStates[fileIdx] != FileState.None) continue;  // skip already-scheduled

            string fileName = dracoFiles[fileIdx];
            string outPath  = Application.persistentDataPath + "/Downloads/" + fileName;
            appArgs += $" -o {outPath} {fullPath}{fileName}";
            fileStates[fileIdx] = FileState.Downloading;
            count++;
        }

        Debug.Log($"[DracoCurl] Slot {slot} downloading batch starting at {batchStart} " +
                  $"({count} files). In-flight batches: {inFlightBatches}.");

        appLauncher.StartProcess(slot, "curl.exe", appArgs, OnBatchFinished);
    }

    /// <summary>Called by AppLauncher when a curl process writes to stdout (i.e. finishes).</summary>
    private void OnBatchFinished(int slot)
    {
        int batchStart = slotBatchStart[slot];
        int marked     = 0;

        for (int i = 0; i < batchSize; i++)
        {
            int fileIdx = (batchStart + i) % numberOfFiles;
            if (fileStates[fileIdx] == FileState.Downloading)
            {
                fileStates[fileIdx] = FileState.Downloaded;
                marked++;
            }
        }

        totalDownloaded += marked;
        inFlightBatches--;
        slotActive[slot]     = false;
        slotBatchStart[slot] = -1;

        Debug.Log($"[DracoCurl] Slot {slot} finished. {marked} files now Downloaded. " +
                  $"Total downloaded: {totalDownloaded} | In-flight batches: {inFlightBatches}.");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Decode stage
    // ═══════════════════════════════════════════════════════════════

    private void TickDecode()
    {
        // Limit simultaneous decode tasks to avoid thread overload
        int maxConcurrentDecodes = parallelCurls * 2;

        while (decodingInFlight < maxConcurrentDecodes &&
               loadedMeshes.Count < maxLoadedMeshes &&
               fileStates[nextDecodeIndex] == FileState.Downloaded)
        {
            int decodeIdx = nextDecodeIndex;
            nextDecodeIndex = (nextDecodeIndex + 1) % numberOfFiles;
            decodingInFlight++;

            DecodeMeshAsync(dracoFiles[decodeIdx], decodeIdx);
        }
    }

    private async void DecodeMeshAsync(string fileName, int fileIndex)
    {
        string filePath = Application.persistentDataPath + "/Downloads/" + fileName;
        byte[] data     = ReadFileSafe(filePath);

        if (data != null)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            await DracoDecoder.DecodeMesh(meshDataArray[0], data);
            data = null;

            Mesh tempMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);

            // Maintain decode ordering: wait until previous file is done
            while (fileIndex != pendingDecodePosition + 1)
            {
                await Task.Delay(1);
            }

            loadedMeshes.Enqueue(tempMesh);
        }
        else
        {
            // File missing – skip slot in ordering
            await Task.Delay(1);
        }

        // Advance ordering cursor
        pendingDecodePosition = (fileIndex == numberOfFiles - 1) ? -1 : fileIndex;

        fileStates[fileIndex] = FileState.Decoded;
        totalDecoded++;
        decodingInFlight--;

        Debug.Log($"[DracoCurl] Decoded file #{fileIndex} '{fileName}'. " +
                  $"Queue size: {loadedMeshes.Count} | Total decoded: {totalDecoded}.");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Playback stage
    // ═══════════════════════════════════════════════════════════════

    private void TickPlayback()
    {
        if (!playerReady) return;
        if (loadedMeshes.Count == 0) return;
        if (fileStates[playbackIndex] != FileState.Decoded) return;

        playerReady = false;

        // Reset file state so the slot can be reused on the next loop
        fileStates[playbackIndex] = FileState.None;
        playbackIndex = (playbackIndex + 1) % numberOfFiles;

        DestroyImmediate(currentMesh);
        loadedMeshes.TryDequeue(out currentMesh);
        totalPlayed++;

        PlayFrame();
    }

    private async void PlayFrame()
    {
        particlesScript.Set(currentMesh);

        float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;
        int   delay     = (int)Math.Round(Math.Max(inverseFPS - elapsedMs, 1f));
        await Task.Delay(delay);

        startTime   = Time.realtimeSinceStartup;
        playerReady = true;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Utilities
    // ═══════════════════════════════════════════════════════════════

    private byte[] ReadFileSafe(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"[DracoCurl] File not found: {filePath}");
            return null;
        }
        try
        {
            return File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DracoCurl] Error reading '{filePath}': {ex.Message}");
            return null;
        }
    }

    private void LogStatsIfNeeded()
    {
        if (Time.realtimeSinceStartup - lastStatsTime < 5f) return;
        lastStatsTime = Time.realtimeSinceStartup;

        Debug.Log($"[DracoCurl] ── Stats ──────────────────────────────────────\n" +
                  $"  Downloaded : {totalDownloaded} files | In-flight batches: {inFlightBatches}\n" +
                  $"  Decoded    : {totalDecoded} files  | Decoding in-flight: {decodingInFlight}\n" +
                  $"  Played     : {totalPlayed} frames  | Mesh queue: {loadedMeshes.Count}/{maxLoadedMeshes}\n" +
                  $"  Curl slots : {parallelCurls} × {batchSize} files/batch\n" +
                  $"─────────────────────────────────────────────────────");
    }

    // ─────────────────────────────────────────────────────────────
    //  Certificate bypass (self-signed servers)
    // ─────────────────────────────────────────────────────────────

    public class BypassCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
}
