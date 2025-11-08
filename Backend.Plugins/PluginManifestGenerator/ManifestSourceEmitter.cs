using System.Collections.Generic;
using System.Text;

namespace PluginManifestGenerator;

public static class ManifestSourceEmitter
{
    public static string EmitYaml(PluginManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"plugin: {manifest.PluginClassName}");
        EmitChannels("inputChannels", manifest.InputChannels, sb);
        EmitChannels("outputChannels", manifest.OutputChannels, sb);
        return sb.ToString();
    }

    private static void EmitChannels(string label, List<ChannelManifest> channels, StringBuilder sb)
    {
        sb.AppendLine($"{label}:");
        foreach (var ch in channels)
        {
            sb.AppendLine("  - type: " + ch.TypeName);
            sb.AppendLine("    description: " + ch.Description);
            sb.AppendLine("    fields:");
            foreach (var f in ch.Fields)
            {
                sb.AppendLine("      - name: " + f.Name);
                sb.AppendLine("        type: " + f.Type);
            }
        }
    }
}
