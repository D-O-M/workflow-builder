namespace Backend.Utilities;

public static class PluginEntryPointExecutablePermissionUtility
{
    private const UnixFileMode ExecuteMode =
        UnixFileMode.UserExecute |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherExecute;

    public static void EnsureExecutable(FileInfo entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        entryPoint.Refresh();
        if (!entryPoint.Exists)
        {
            throw new FileNotFoundException("The plugin entry point was not found after bundle extraction.", entryPoint.FullName);
        }

        var currentMode = File.GetUnixFileMode(entryPoint.FullName);
        var executableMode = currentMode | ExecuteMode;
        if (currentMode != executableMode)
        {
            File.SetUnixFileMode(entryPoint.FullName, executableMode);
        }
    }
}
