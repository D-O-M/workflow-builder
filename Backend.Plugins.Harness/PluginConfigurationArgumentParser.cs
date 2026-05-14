namespace Backend.Plugins.Harness;

internal static class PluginConfigurationArgumentParser
{
    public static IReadOnlyList<string> GetConfigPaths(IReadOnlyList<string> args)
    {
        var paths = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!string.Equals(arg, "--config", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("--config must be followed by a JSON configuration file path.");
            }

            paths.Add(Path.GetFullPath(args[index + 1]));
            index++;
        }

        return paths;
    }
}
