using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Backend.Interfaces.Repositories;
using Backend.PluginLoader.DTOs;
using Microsoft.Extensions.Hosting;

namespace Backend.PluginLoader.Services;

internal class PluginReceptionService : BackgroundService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IWorkflowTaskRepository _workflowTaskRepository;

    public PluginReceptionService(
        BlobContainerClient blobContainerClient,
        IWorkflowTaskRepository workflowTaskRepository)
    {
        _blobContainerClient = blobContainerClient;
        _workflowTaskRepository = workflowTaskRepository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workflowTasks = (await _workflowTaskRepository.GetAll(stoppingToken))
                .GroupBy(t => t.Name)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Version).ToHashSet());

            await foreach (var blob in _blobContainerClient.GetBlobsAsync(
                             traits: BlobTraits.None,
                             states: BlobStates.None,
                             cancellationToken: stoppingToken))
            {
                Console.WriteLine($"Found blob: {blob.Name}");

                var (name, version) = ExtractDetailsFromBlobName(blob.Name);

                if (!workflowTasks.ContainsKey(name) || !workflowTasks[name].Contains(version))
                {
                    await HandleNewPlugin(blob.Name, stoppingToken);
                }
            }

            Thread.Sleep(5000);
        }
    }

    public async Task HandleNewPlugin(string blobName, CancellationToken stoppingToken)
    {
        var blobClient = _blobContainerClient.GetBlobClient(blobName);

        // Create a unique working folder for this bundle (so we can run the app later)
        var (pluginName, pluginVersion) = ExtractDetailsFromBlobName(blobName);
        var workDir = Path.Combine(Path.GetTempPath(), $"plugin_{Sanitize(pluginName)}_{pluginVersion}");
        if (Directory.Exists(workDir))
        {
            // Fresh extract (you can switch to reuse if you want caching)
            Directory.Delete(workDir, recursive: true);
        }
        Directory.CreateDirectory(workDir);

        // 1) Download -> open as ZIP -> extract to workDir
        await using (var zipStream = await blobClient.OpenReadAsync(cancellationToken: stoppingToken))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            ExtractArchiveToDirectory(archive, workDir, overwrite: true);
        }

        // 2) Read manifest.json from the extracted folder
        var manifestPath = Path.Combine(workDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"No manifest.json found in bundle: {blobName}");
            return;
        }

        ManifestFile? manifest;
        await using (var manifestFs = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<ManifestFile>(
                manifestFs,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                stoppingToken);
        }

        if (manifest is null)
        {
            Console.WriteLine($"Failed to parse manifest in {blobName}");
            return;
        }

        // 3) Read schemas: prefer aggregated schemas.json, else fold schemas/*.json into an aggregate JsonNode
        string schemasJsonPath = Path.Combine(workDir, "schemas.json");
        JsonNode? schemasAggregateNode = null;

        if (File.Exists(schemasJsonPath))
        {
            using var schemasFs = File.OpenRead(schemasJsonPath);
            schemasAggregateNode = await JsonNode.ParseAsync(schemasFs, cancellationToken: stoppingToken);
            if (schemasAggregateNode is null)
            {
                Console.WriteLine($"Failed to parse schemas.json in {blobName}");
                return;
            }
        }
        else
        {
            var schemasDir = Path.Combine(workDir, "schemas");
            if (!Directory.Exists(schemasDir))
            {
                Console.WriteLine($"No schemas.json or schemas/ folder found in {blobName}");
                return;
            }

            // Build an aggregate { "components": { "schemas": { "<name>": <json> ... } } }
            var components = new JsonObject();
            foreach (var file in Directory.EnumerateFiles(schemasDir, "*.json", SearchOption.AllDirectories))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                using var fs = File.OpenRead(file);
                var node = await JsonNode.ParseAsync(fs, cancellationToken: stoppingToken);
                if (node is not null)
                {
                    components[key] = node;
                }
            }

            schemasAggregateNode = new JsonObject
            {
                ["components"] = new JsonObject
                {
                    ["schemas"] = components
                }
            };
        }

        // If you have a SchemasFile model, you can deserialize into it.
        // Otherwise, keep schemasAggregateNode as a JsonNode for downstream use.
        // Assume models are correct: try to bind; if not, keep the node.
        SchemasFile? schemas = null;
        try
        {
            schemas = schemasAggregateNode is null
                ? null
                : schemasAggregateNode.Deserialize<SchemasFile>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch
        {
            // If your SchemasFile is not an exact match for the aggregate shape, you can
            // keep `schemasAggregateNode` and pass it to the next stage as raw JsonNode.
        }

        Console.WriteLine($"Extracted to: {workDir}");
        Console.WriteLine($"Parsed manifest with {manifest.InputChannels?.Count ?? 0} input(s) and {manifest.OutputChannels?.Count ?? 0} output(s).");

        // At this point:
        //  - workDir\app\ contains the published app you can run (exe/dll + deps)
        //  - workDir\manifest.json is parsed into `manifest`
        //  - schemas are in `schemas` (typed) or `schemasAggregateNode` (JsonNode)

        // Example: determine app entry path for the next stage (running the app)
        var appDir = Path.Combine(workDir, "app");
        var exePath = Directory.EnumerateFiles(appDir, "*.exe").FirstOrDefault();
        var dllPath = Directory.EnumerateFiles(appDir, "*.dll").FirstOrDefault();

        Console.WriteLine(exePath is not null
            ? $"App entry (exe): {exePath}"
            : dllPath is not null ? $"App entry (dll): {dllPath}" : "No app entry found.");

        // Persist metadata / schemas if needed:
        // await _workflowTaskRepository.CreateTaskSchema(manifest, stoppingToken);
        // (You can also save `schemasAggregateNode` or `schemas` as appropriate.)
    }

    private static (string name, Version version) ExtractDetailsFromBlobName(string blobName)
    {
        var match = Regex.Match(blobName, @"^(?<name>.+)_(?<version>\d+(\.\d+){0,3})\.zip$");
        if (!match.Success)
            throw new FormatException("Blob name must be {Name}_{Version}");

        string name = match.Groups["name"].Value;
        string version = match.Groups["version"].Value;

        if (!Version.TryParse(version, out var parsedVersion))
            throw new FormatException($"Invalid version format encountered while processing blob name. Name: {name} | Version: {version}");

        return (name, parsedVersion);
    }

    private static string Sanitize(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');
        return value;
    }

    /// <summary>
    /// Extracts all entries from a ZipArchive to a directory, safely (prevents path traversal).
    /// </summary>
    private static void ExtractArchiveToDirectory(ZipArchive archive, string destinationDirectoryFullPath, bool overwrite)
    {
        destinationDirectoryFullPath = Path.GetFullPath(destinationDirectoryFullPath);
        foreach (var entry in archive.Entries)
        {
            var fullPath = Path.Combine(destinationDirectoryFullPath, entry.FullName);

            // Normalize and guard against zip-slip/path traversal
            var normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(destinationDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
                continue; // skip suspicious entry

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry
                Directory.CreateDirectory(normalized);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(normalized)!);
            if (overwrite || !File.Exists(normalized))
            {
                using var entryStream = entry.Open();
                using var outStream = File.Create(normalized);
                entryStream.CopyTo(outStream);
            }
        }
    }
}
