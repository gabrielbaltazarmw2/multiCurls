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
    private string fullPath;
    public string HostPath;
    private string _http = "https://";
    private string _port = "443";

    [SerializeField]
    private string[] _files;
    private int currentFiles = 0;

    [SerializeField]
    private int[] sliceAddressList;
    //private float[] sliceTimestampList;

    private int currentSlice = 0;

    //private bool CheckSlicesTimestampEnabled = false;

    //[SerializeField]
    //private SliceGraphicsChanger _changer;

    //[SerializeField]
    //private float _TimestampThreshold = 1.0f;

    public float FPS = 30;
    private float inverseFPS;
    public bool isLoop = true;

    public int batchSize = 30;
    private int loadedFilesMaxSize = 90;
    private int currentLoadedNumber = 0;
    private int currentPlayingNumber = 0;


    private int playIndex, currentPosition;
    private string[] dracoFiles;

    //public StatusMonitor monitor;
    public AnimationFPSCounter counter;
    //public TextMeshProUGUI downloadTimerText;

    public AppLauncher appLauncher;

    private Queue<Mesh> loadedMeshes;
    private Mesh currentMesh;

    private float startTime = 0;

    [SerializeField]
    private readiness[] filesReadinessStatus;

    private bool haltDownloading = false;
    private bool playerReady = true;
    [SerializeField]
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
        //sliceTimestampList = new float[sliceAddressList.Count()];
        loadedMeshes = new Queue<Mesh>();
        PlayIndex = 0;
        currentPosition = -1;
        inverseFPS = 1000 / FPS;
        currentMesh = new Mesh();
        downloadingFilesFrom = 0;
        currentLoadedNumber = 0;
        currentPlayingNumber = 0;
        //ResetTimestamps();
    }

    void UpdateDracoFiles()
    {
        //StartCoroutine(GetFilesFromHTTP(fullPath, (val) => { dracoFiles = val; }));

        dracoFiles = new string[numberOfFiles];

        if (filesReadinessStatus.Count() != numberOfFiles)
        {
            filesReadinessStatus = new readiness[numberOfFiles];

            for (int i = 0; i < numberOfFiles; i++)
            {
                filesReadinessStatus[i] = readiness.None;
            }
        }

        for (int i = 0; i < numberOfFiles; i++) //Disgustingly hardcoded solution
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


    async void PlaySingleFile()
    {
        playerReady = false;
        particlesScript.Set(currentMesh);


        float elapsedS = Time.realtimeSinceStartup - startTime;
        float elapsedMS = elapsedS * 1000;

        //Debug.Log(elapsedMS);
        if (elapsedMS < inverseFPS)
        {
            int delay = (int)Math.Round(inverseFPS - elapsedMS);
            //Debug.Log("Must wait " + delay);
            await Task.Delay(delay);
        }
        else
        {
            await Task.Delay(1);
        }
        //counter.Tick();
        startTime = Time.realtimeSinceStartup;


        playerReady = true;
    }


    async void ReadMeshFromFile(string fileName, int position)
    {
        byte[] stream = ReadStreamFromDownloadedFile(fileName, Application.persistentDataPath + "/Downloads/");


        if (stream != null)
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);

            await DracoDecoder.DecodeMesh(meshDataArray[0], stream);

            stream = null;

            Mesh tempMesh = new Mesh();

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, tempMesh);
            while (position != currentPosition + 1)
            {
                await Task.Delay(1);
            }

            loadedMeshes.Enqueue(tempMesh);
        }
        else
        {
            await Task.Delay(1);
        }

        if (position == dracoFiles.Length - 1)
        {
            currentPosition = -1;
        }
        else
        {
            currentPosition = position;
        }
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

            if (position != 0)
            {
                while (filesReadinessStatus[position - 1] == readiness.Downloaded)
                {
                    await Task.Delay(1);
                }
            }
            else
            {
                while (filesReadinessStatus[numberOfFiles - 1] == readiness.Downloaded)
                {
                    await Task.Delay(1);
                }
            }

            loadedMeshes.Enqueue(tempMesh);
        }
        else
        {
            await Task.Delay(1);
        }
        downloadedCount -= 1;
        filesReadinessStatus[position] = readiness.Loaded;
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
        //_changer.ChangeSlice(currentSlice);
    }

    public void SetQualityFromQualityList(int quality)
    {
        currentFiles = quality;
        ResetHostPath();
    }

    private void ResetHostPath()
    {
        fullPath = _http + HostPath + ":" + _port + "/" + _files[currentFiles];
        //UpdateDracoFiles();
    }

    public void Reconnect()
    {
        UpdateDracoFiles();

        //appLauncher.KillProcess();

        //haltDownloading = false;
        //PlayIndex = 0;
        //currentPosition = -1;
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
        //CheckSlicesTimestampEnabled = !CheckSlicesTimestampEnabled;
    }

    /*
    public void ResetTimestamps()
    {
        for (int i = 0; i < sliceTimestampList.Length; i++)
        {
            sliceTimestampList[i] = -1;
        }
    }
    */

    public void AdvanceBatch()
    {
        for (int i = downloadingFilesFrom; (i < downloadingFilesFrom + batchSize && i < numberOfFiles); i++)
        {
            filesReadinessStatus[i] = readiness.Downloaded;
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
        //Starts the requests to download files
        if (downloadedCount < loadedFilesMaxSize && !haltDownloading && filesReadinessStatus[downloadingFilesFrom] == readiness.None)
        {
            haltDownloading = true;
            string appArgs = "--http3 --parallel";
            for (int i = downloadingFilesFrom; (i < downloadingFilesFrom + batchSize && i < numberOfFiles); i++)
            {
                string filepath = dracoFiles[i];

                appArgs += " -o " + Application.persistentDataPath + "/Downloads/" + filepath + " " + fullPath + filepath;
            }
            appLauncher.StartProcess("curl.exe", appArgs);
        }


        if (loadedMeshes.Count < loadedFilesMaxSize && filesReadinessStatus[currentLoadedNumber] == readiness.Downloaded) //|| currentLoadedNumber > numberOfFiles - batchSize
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
        //Play the read files to the user
        if (playerReady && filesReadinessStatus[currentPlayingNumber] == readiness.Loaded)
        {
            playerReady = false;
            filesReadinessStatus[currentPlayingNumber] = readiness.None;
            if (currentPlayingNumber < numberOfFiles - 1)
            {
                currentPlayingNumber += 1;
            }
            else
            {
                currentPlayingNumber = 0;
            }


            DestroyImmediate(currentMesh);
            loadedMeshes.TryDequeue(out currentMesh);
            PlaySingleFile();

        }
    }
}


