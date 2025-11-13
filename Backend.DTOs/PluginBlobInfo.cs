namespace Backend.DTOs;

public readonly record struct PluginBlobInfo(
    string BlobName,
    string PluginName,
    Version Version);
