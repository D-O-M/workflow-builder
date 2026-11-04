using System;
using System.IO;
using Backend.Utilities;
using Shouldly;
using TUnit.Core;

namespace Backend.Engine.Tests;

public sealed class PluginEntryPointExecutablePermissionUtilityTests
{
    [Test]
    public void EnsureExecutable_adds_execute_permissions_on_Linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("Linux execute permission validation requires Linux.");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"workflow-builder-plugin-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "test");
            const UnixFileMode originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, originalMode);

            PluginEntryPointExecutablePermissionUtility.EnsureExecutable(new FileInfo(path));

            File.GetUnixFileMode(path).ShouldBe(originalMode |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
