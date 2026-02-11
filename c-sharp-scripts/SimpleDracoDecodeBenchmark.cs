using UnityEngine;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Draco;
using Debug = UnityEngine.Debug;

public class SimpleDracoDecodeBenchmark : MonoBehaviour
{
    // ==========================
    // CONFIGURAÇÃO DIRETO NO CÓDIGO
    // ==========================

    // Caminho da pasta com os .drc
    // Coloque aqui a pasta que você usou no teste:
    // Exemplo:
    //   @"C:\Users\Rafael\Desktop\PointClouds\Longdress\draco\draco-qp10"
    private const string INPUT_FOLDER =
        @"C:\Users\Rafael\Desktop\PointClouds\Longdress\draco\draco-qp10";

    // Padrão de busca dos arquivos
    private const string SEARCH_PATTERN = "*.drc";

    // Limitar o número de arquivos (0 = todos)
    private const int MAX_FILES = 0;

    // Destruir a mesh depois de decodificar (evita acumular na memória)
    private const bool DESTROY_MESH_AFTER_DECODE = true;


    // ==========================
    // ENTRY POINT
    // ==========================

    private async void Start()
    {
        // 1) Verificar se a pasta existe
        if (!Directory.Exists(INPUT_FOLDER))
        {
            Debug.LogError($"[DecodeBenchmark] Input folder not found: {INPUT_FOLDER}");
            return;
        }

        // 2) Descobrir arquivos .drc
        var files = Directory
            .GetFiles(INPUT_FOLDER, SEARCH_PATTERN, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            Debug.LogError($"[DecodeBenchmark] No .drc files found in {INPUT_FOLDER}");
            return;
        }

        if (MAX_FILES > 0 && MAX_FILES < files.Count)
        {
            files = files.Take(MAX_FILES).ToList();
        }

        Debug.Log($"[DecodeBenchmark] Folder: {INPUT_FOLDER}");
        Debug.Log($"[DecodeBenchmark] Files found: {files.Count}");
        Debug.Log($"[DecodeBenchmark] DestroyMeshAfterDecode: {DESTROY_MESH_AFTER_DECODE}");

        var globalSw = Stopwatch.StartNew();

        // 3) Loop de decodificação sequencial
        int index = 0;
        foreach (var filePath in files)
        {
            index++;
            await DecodeSingleFile(filePath, index, files.Count);
        }

        globalSw.Stop();
        Debug.Log($"[DecodeBenchmark] Finished. Total time: {globalSw.Elapsed.TotalMilliseconds:F3} ms");
    }

    // ==========================
    // DECODE DE UM ÚNICO ARQUIVO
    // ==========================

    private async Task DecodeSingleFile(string filePath, int index, int total)
    {
        string fileName = Path.GetFileName(filePath);

        // Ler arquivo inteiro em memória
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(filePath);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DecodeBenchmark] Error reading file {fileName}: {ex.Message}");
            return;
        }

        // Alocar buffer de mesh
        var meshDataArray = Mesh.AllocateWritableMeshData(1);

        // Medir APENAS tempo de decodificação Draco
        var swDecode = Stopwatch.StartNew();
        try
        {
            await DracoDecoder.DecodeMesh(meshDataArray[0], bytes);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DecodeBenchmark] Error decoding {fileName}: {ex.Message}");
            meshDataArray.Dispose();
            return;
        }
        swDecode.Stop();

        // Criar Mesh Unity a partir dos dados decodificados
        Mesh mesh = new Mesh();
        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);

        if (DESTROY_MESH_AFTER_DECODE)
        {
            Object.Destroy(mesh);
        }

        double decodeMs = swDecode.Elapsed.TotalMilliseconds;

        // Log simples com o tempo de decode daquele arquivo
        Debug.Log($"[DECODE] {index}/{total} file={fileName} decode_ms={decodeMs:F3}");


        // Pequena pausa para não travar demais o Editor (opcional)
        await Task.Yield();
    }
}
