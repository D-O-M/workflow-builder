using Backend.DTOs;

namespace Backend.Interfaces.Utilities;

public interface IPluginBlobNameParsingUtility
{
    PluginBlobInfo Parse(string blobName);
}
