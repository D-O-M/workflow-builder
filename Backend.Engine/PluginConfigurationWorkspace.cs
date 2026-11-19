using System.Security.AccessControl;
using System.Security.Principal;

namespace Backend.Engine;

// A live Engine keeps its owner file open. A later Engine can collect a crashed
// Engine's directories without deleting files still in use by another replica.
internal sealed class PluginConfigurationWorkspace : IDisposable
{
    private const string OwnerFileName = ".workflowbuilder-plugin-config-owner";
    private readonly DirectoryInfo _directory;
    private readonly FileStream _owner;

    public PluginConfigurationWorkspace(string root)
    {
        var parent = CreatePrivateDirectory(Path.GetFullPath(root));
        foreach (var stale in parent.EnumerateDirectories("engine-*"))
        {
            if (!Guid.TryParseExact(stale.Name["engine-".Length..], "N", out _) ||
                stale.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }
            try
            {
                using (File.Open(Path.Combine(stale.FullName, OwnerFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // A live Engine holds an exclusive handle.
                }
                stale.Delete(true);
            }
            catch (IOException) { /* Active, being created, or already removed. */ }
            catch (UnauthorizedAccessException) { /* Owned by another OS identity. */ }
        }
        _directory = CreatePrivateDirectory(Path.Combine(parent.FullName, $"engine-{Guid.NewGuid():N}"));
        _owner = File.Open(Path.Combine(_directory.FullName, OwnerFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public DirectoryInfo CreateLaunchDirectory() =>
        CreatePrivateDirectory(Path.Combine(_directory.FullName, Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        _owner.Dispose();
        try { _directory.Delete(true); }
        catch (DirectoryNotFoundException) { }
    }

    private static DirectoryInfo CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        return FileSystemAclExtensions.CreateDirectory(security, path);
    }
}
