namespace Backend.DTOs;

public enum ManifestChannelType
{
    Input,
    Output
};

public record Manifest(
    string Name,
    string Path,
    Version Version,
    List<ManifestChannelDefinition> Channels);

public record ManifestChannelDefinition(
    int Ordinal,
    string Name,
    ManifestChannelType Type,
    List<ManifestChannelParameterDefinition> Parameters);

public record ManifestChannelParameterDefinition(
    string Name,
    string Type);
