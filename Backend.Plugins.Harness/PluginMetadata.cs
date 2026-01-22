using System;
using System.Collections.Generic;
using System.Text;

namespace Backend.Plugins.Harness;

internal record PluginMetadata
{
    public bool HasGlobalState { get; init; }
    public bool HasInput { get; init; }

}
