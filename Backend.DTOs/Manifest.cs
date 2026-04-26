namespace Backend.Models;

public enum ManifestChannelType
{
    Input,
    Output
};

public record Manifest(
    string Name,
    string Path,
    Version Version,
    ManifestModelSchema? GlobalState,
    List<ManifestChannelDefinition> Channels);

public record ManifestModelSchema(
    string Name,
    string ModelTypeName,
    List<ManifestParameterDefinition> Parameters);

public record ManifestChannelDefinition(
    int Ordinal,
    string Name,
    string ModelTypeName,
    List<ManifestParameterDefinition> Parameters,
    ManifestChannelType ChannelType)
    : ManifestModelSchema(Name, ModelTypeName, Parameters);

public record ManifestParameterDefinition(
    string Name,
    string Type);
