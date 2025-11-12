using System;

namespace Backend.PluginLoader.Services;

internal readonly record struct PluginBlobInfo(string BlobName, string PluginName, Version Version);
