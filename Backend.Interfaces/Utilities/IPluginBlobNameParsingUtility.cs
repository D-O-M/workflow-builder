using Backend.Models;

namespace Backend.Interfaces.Utilities;

public interface IPluginBlobNameParsingUtility
{
    PluginBlobInfo Parse(string blobName);
}
