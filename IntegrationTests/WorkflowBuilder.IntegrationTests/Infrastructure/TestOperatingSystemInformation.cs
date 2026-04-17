using Backend.Engine.Debugging;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class TestOperatingSystemInformation : IOperatingSystemInformation
{
    public bool IsWindows() => OperatingSystem.IsWindows();

    public bool IsLinux() => OperatingSystem.IsLinux();
}
