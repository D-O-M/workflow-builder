using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
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

    public async Task HandleNewPlugin(string blobName, CancellationToken stoppingToken)
    {
        var blobClient = _blobContainerClient.GetBlobClient(blobName);

        // Download full archive (streaming) and open as ZIP
        await using var zipStream = await blobClient.OpenReadAsync(cancellationToken: stoppingToken); // full archive anyway; OpenRead streams ranges under the hood
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        // Locate the manifest entry
        var manifestEntry = archive.Entries
            .FirstOrDefault(e => e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry is null)
        {
            Console.WriteLine($"No manifest.json found in {blobName}");
            return;
        }

        // Parse JSON
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(
            manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken: stoppingToken);

        if (manifest is null)
        {
            Console.WriteLine($"Failed to parse manifest in {blobName}");
            return;
        }

        Console.WriteLine("Parsed Manifest: {Manifest}", JsonSerializer.Serialize(manifest));

        await _workflowTaskRepository.CreateWorkflowTask(manifest, stoppingToken);
    }

    private static (string name, Version version) ExtractDetailsFromBlobName(string blobName)
    {
        var match = Regex.Match(blobName, @"^(?<name>.+)_(?<version>\d+(\.\d+){0,3})$");

        if (!match.Success)
            throw new FormatException("Blob name must be {Name}_{Version}");

        string name = match.Groups["name"].Value;
        string version = match.Groups["version"].Value;

        // Optional – treat it as a structured Semantic Version:
        if (!Version.TryParse(version, out var parsedVersion))
            throw new FormatException($"Invalid version format encountered while processing blob name. Name: {name} | Version: {version}");

        return (name, parsedVersion);
    }
}
