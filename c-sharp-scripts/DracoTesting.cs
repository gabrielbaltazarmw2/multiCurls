using System.Collections;
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
    [SerializeField]
    private string fullPath;
    public string HostPath;
    private string _http = "https://";
    private string _port = "443";

    [SerializeField]
    private string[] _files;
    private int currentFiles = 0;

    [SerializeField]
    private int[] sliceAddressList;

    private int currentSlice = 0;

    public float FPS = 30;
    private float inverseFPS;
    public bool isLoop = true;

    public int batchSize = 30;
    private int loadedFilesMaxSize = 90;
    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = -1; // Changed to -1 to ensure first frame triggers update

    private int playIndex, currentPosition;
    private string[] dracoFiles;

    public AnimationFPSCounter counter;
    public AppLauncher appLauncher;

    // --- REPLACED QUEUE WITH ARRAY FOR RANDOM ACCESS ---
    private Mesh[] loadedMeshesArray; 
    private Mesh currentMesh;
    private int meshesReadyCount = 0; // Tracks how many decoded meshes are in RAM

    // --- REAL-TIME PLAYHEAD VARIABLES ---
    private bool isPlaying = false;
    private float streamStartTime = 0;

    [SerializeField]
    private readiness[] filesReadinessStatus;

    private bool haltDownloading = false;
    private int numberOfFiles = 300;
    private int downloadingFilesFrom;
    private int downloadedCount = 0;

    public int PlayIndex { get => playIndex; set => playIndex = value; }

    public DracoToParticles particlesScript;

    private enum readiness
    {
        None,
        Downloaded,
        Loaded
    }

    private void OnEnable()
    {
        PlayIndex = 0;
        currentPosition = -1;
        inverseFPS = 1000 / FPS;
        currentMesh = new Mesh();
        downloadingFilesFrom = 0;
        currentLoadedNumber = 0;
        currentPlayingNumber = -1;
        isPlaying = false;
        meshesReadyCount = 0;
    }

    void UpdateDracoFiles()
    {
        dracoFiles = new string[numberOfFiles];

        // Initialize our random-access mesh array
        loadedMeshesArray = new Mesh[numberOfFiles];

        if (filesReadinessStatus == null || filesReadinessStatus.Count() != numberOfFiles)
        {
            filesReadinessStatus = new readiness[numberOfFiles];

            for (int i = 0; i < numberOfFiles; i++)
            {
                filesReadinessStatus[i] = readiness.None;
            }
        }

        for (int i = 0; i < numberOfFiles; i++) // Disgustingly hardcoded solution :)
        {
            dracoFiles[i] = (1000 + i) + ".drc";
        }

        loadedFilesMaxSize = Mathf.Min(numberOfFiles / 3, batchSize * 2);
    }

    public class BypassCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true; // Accept all certificates
        }
    }

    // --- SIMPLIFIED PLAYBACK FUNCTION ---
    // Timing is now handled by Update(), so this just instantly swaps the mesh.
    void PlaySpecificFile(int index)
    {        
        if (currentMesh != null) 
        {
            DestroyImmediate(currentMesh);
        }

        currentMesh = loadedMeshesArray[index];
        particlesScript.Set(currentMesh);
        
        // Remove from array so we don't hold the reference
        loadedMeshesArray[index] = null; 
    }

    async void ReadSingleMeshFromFile(string fileName, int position)
    {
        byte[] stream = ReadStreamFromDownloadedFile(fileName, Application.persistentDataPath + "/Downloads/");

        if (stream != null)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);

            await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

            stream = null;

            Mesh tempMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);
            
            // --- NO LONGER WAITING FOR SEQUENTIAL DECODE ---
            // Just slot the mesh directly into its array position
            loadedMeshesArray[position] = tempMesh;
            
            downloadedCount -= 1;
            meshesReadyCount += 1;
            filesReadinessStatus[position] = readiness.Loaded;
        }
        else
        {
            await Task.Delay(1);
        }
    }

    byte[] ReadStreamFromDownloadedFile(string fileName, string filePath)
    {
        if (filePath == null)
        {
            filePath = Application.persistentDataPath + "/Downloads/";
        }
        if (File.Exists(filePath + fileName))
        {
            try
            {
                return File.ReadAllBytes(filePath + fileName);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error reading file {filePath + fileName}: {ex.Message}");
                return null;
            }
        }
        else
        {
            Debug.LogWarning($"File not found: {filePath + fileName}");
            return null;
        }
    }

    public void SetNewIP(string newIP)
    {
        HostPath = newIP;
        ResetHostPath();
    }

    public void SetNewPort(string newPort)
    {
        Debug.Log("Not changing slice, undefined behavior");
        _port = newPort;
        ResetHostPath();
    }

    public void SetPortFromSliceList(int slice)
    {
        currentSlice = slice;
        _port = sliceAddressList[currentSlice].ToString();
        ResetHostPath();
    }

    public void SetQualityFromQualityList(int quality)
    {
        currentFiles = quality;
        ResetHostPath();
    }

    private void ResetHostPath()
    {
        fullPath = _http + HostPath + ":" + _port + "/" + _files[currentFiles];
    }

    public void Reconnect()
    {
        UpdateDracoFiles();
    }

    public void ChangeFramerate(string newFramerate)
    {
        int checkOutput;
        if (Int32.TryParse(newFramerate, out checkOutput))
        {
            FPS = checkOutput;
        }
    }

    public void SwitchCheckTimestampsFunction()
    {
    }

    public void AdvanceBatch()
    {
        for (int i = 0; i < batchSize; i++)
        {
            filesReadinessStatus[i + downloadingFilesFrom] = readiness.Downloaded;
            downloadedCount += 1;
        }

        if (downloadingFilesFrom + batchSize < numberOfFiles)
        {
            downloadingFilesFrom += batchSize;
        }
        else
        {
            downloadingFilesFrom = 0;
        }

        haltDownloading = false;
    }

    private void Update()
    {
        if (dracoFiles == null)
        {
            return;
        }

        // 1. Start the curl requests to download files
        if (downloadedCount < loadedFilesMaxSize && !haltDownloading && filesReadinessStatus[downloadingFilesFrom] == readiness.None)
        {
            haltDownloading = true;
            string appArgs = "--http3 --parallel";
            for (int i = 0; i < batchSize; i++)
            {
                string filepath = dracoFiles[i + downloadingFilesFrom];
                appArgs += " -o " + Application.persistentDataPath + "/Downloads/" + filepath + " " + fullPath + filepath;
            }
            appLauncher.StartProcess("curl.exe", appArgs);
        }

        // 2. Decode files that have finished downloading
        if (meshesReadyCount < loadedFilesMaxSize && filesReadinessStatus[currentLoadedNumber] == readiness.Downloaded) 
        {
            ReadSingleMeshFromFile(dracoFiles[currentLoadedNumber], currentLoadedNumber);
            
            if (currentLoadedNumber < numberOfFiles - 1)
            {
                currentLoadedNumber += 1;
            }
            else
            {
                currentLoadedNumber = 0;
            }
        }

        // --- 3. REAL-TIME PLAYHEAD CONTROLLER ---

        // Start the clock only when we have a small buffer to prevent instant starving
        if (!isPlaying && filesReadinessStatus.Length > 2 && 
            filesReadinessStatus[0] == readiness.Loaded && 
            filesReadinessStatus[1] == readiness.Loaded)
        {
            isPlaying = true;
            streamStartTime = Time.realtimeSinceStartup;
            Debug.Log("Playback Started!");
        }

        if (isPlaying)
        {
            // What frame SHOULD we be looking at right now?
            float elapsedSeconds = Time.realtimeSinceStartup - streamStartTime;
            int targetFrameIndex = Mathf.FloorToInt(elapsedSeconds * FPS);

            // Handle end of video / looping
            if (targetFrameIndex >= numberOfFiles)
            {
                if (isLoop)
                {
                    // For MOS testing, it's usually better to NOT loop and just record one pass,
                    // but if you need it to loop, we reset the clock.
                    streamStartTime = Time.realtimeSinceStartup;
                    targetFrameIndex = 0;
                    currentPlayingNumber = -1;
                }
                else
                {
                    return; 
                }
            }

            // If the clock has moved to a new frame
            if (targetFrameIndex != currentPlayingNumber)
            {
                // Is the network fast enough? Did it load on time?
                if (filesReadinessStatus[targetFrameIndex] == readiness.Loaded)
                {
                    currentPlayingNumber = targetFrameIndex;
                    PlaySpecificFile(targetFrameIndex);
                    
                    meshesReadyCount -= 1; // We consumed one
                    
                    // Mark this frame and ALL PREVIOUS frames as consumed.
                    // This explicitly drops frames that were delayed by QUIC/tc
                    for (int i = 0; i <= targetFrameIndex; i++)
                    {
                        if (filesReadinessStatus[i] == readiness.Loaded || filesReadinessStatus[i] == readiness.Downloaded)
                        {
                            filesReadinessStatus[i] = readiness.None;
                            
                            // If we are skipping over a mesh that arrived late, destroy it from RAM
                            if (i != targetFrameIndex && loadedMeshesArray[i] != null)
                            {
                                DestroyImmediate(loadedMeshesArray[i]);
                                loadedMeshesArray[i] = null;
                                meshesReadyCount -= 1;
                            }
                        }
                    }
                }
                else
                {
                    // --- THE SKIP OCCURS ---
                    // QUIC is busy recovering a dropped packet due to 'tc'.
                    // The file isn't ready, so we do nothing. The old mesh stays on screen.
                    // We will check again on the next Update().
                }
            }
        }
    }
}

