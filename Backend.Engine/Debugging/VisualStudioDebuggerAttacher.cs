using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Options;
using Process = System.Diagnostics.Process;

namespace Backend.Engine.Debugging;

public class VisualStudioDebuggerAttacher
{
    private readonly ILogger<VisualStudioDebuggerAttacher> _logger;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly DebuggerAutoAttachOptions _options;
    private readonly IVisualStudioInstanceProvider _instanceProvider;
    private readonly IDebuggerAttachmentGuard _attachmentGuard;
    private readonly IAsyncDelay _delay;
    private readonly IOperatingSystemInformation _operatingSystem;

    public VisualStudioDebuggerAttacher(
        ILogger<VisualStudioDebuggerAttacher> logger,
        IHostEnvironment hostEnvironment,
        IOptions<DebuggerAutoAttachOptions> options,
        IVisualStudioInstanceProvider instanceProvider,
        IDebuggerAttachmentGuard attachmentGuard,
        IAsyncDelay delay,
        IOperatingSystemInformation operatingSystem)
    {
        _logger = logger;
        _hostEnvironment = hostEnvironment;
        _options = options.Value;
        _instanceProvider = instanceProvider;
        _attachmentGuard = attachmentGuard;
        _delay = delay;
        _operatingSystem = operatingSystem;
    }

    public async Task TryAttachAsync(Process process, CancellationToken cancellationToken)
    {
        if (!_hostEnvironment.IsDevelopment())
        {
            _logger.LogDebug("Debugger auto-attach is disabled outside of development environment.");
            return;
        }

        if (!_options.Enabled)
        {
            _logger.LogDebug("Debugger auto-attach is disabled by configuration.");
            return;
        }

        if (!_operatingSystem.IsWindows())
        {
            _logger.LogInformation("Debugger auto-attach is only available on Windows.");
            return;
        }

        if (process.HasExited)
        {
            _logger.LogWarning("Debugger auto-attach skipped because the plugin runtime process has already exited.");
            return;
        }

        if (_attachmentGuard.IsDebuggerAttached(process))
        {
            _logger.LogInformation(
                "Debugger auto-attach skipped because the plugin runtime process {ProcessId} already has a debugger.",
                process.Id);
            return;
        }

        var solutionPaths = NormalizePaths(_options.SolutionPaths, OperatingSystem.IsWindows());
        if (solutionPaths.Count == 0)
        {
            _logger.LogInformation("Debugger auto-attach skipped because no candidate solution paths were configured.");
            return;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(0, _options.TimeoutMilliseconds));
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.PollingIntervalMilliseconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var attemptedLaunch = false;
        var launchTargets = NormalizePaths(_options.LaunchSolutionPaths, OperatingSystem.IsWindows());
        if (launchTargets.Count == 0)
        {
            launchTargets.AddRange(solutionPaths);
        }

        while (DateTimeOffset.UtcNow <= deadline && !cancellationToken.IsCancellationRequested)
        {
            var instances = _instanceProvider.GetRunningInstances();
            var matchingInstance = FindMatchingInstance(instances, solutionPaths);
            if (matchingInstance is not null)
            {
                var result = matchingInstance.TryAttachToProcess(process.Id);
                switch (result)
                {
                    case AttachResult.AlreadyAttached:
                        _logger.LogInformation(
                            "Debugger auto-attach skipped because Visual Studio already attached to plugin runtime process {ProcessId}.",
                            process.Id);
                        return;
                    case AttachResult.Attached:
                        _logger.LogInformation(
                            "Attached Visual Studio debugger to plugin runtime process {ProcessId} using solution {Solution}.",
                            process.Id,
                            matchingInstance.SolutionPath);
                        return;
                    case AttachResult.ProcessNotFound:
                        _logger.LogDebug(
                            "Plugin runtime process {ProcessId} not yet visible to Visual Studio instance for solution {Solution}.",
                            process.Id,
                            matchingInstance.SolutionPath);
                        break;
                    case AttachResult.Failed:
                        _logger.LogWarning(
                            "Visual Studio instance for solution {Solution} could not attach to plugin runtime process {ProcessId}.",
                            matchingInstance.SolutionPath,
                            process.Id);
                        break;
                }
            }
            else if (_options.LaunchIfMissing && !attemptedLaunch)
            {
                attemptedLaunch = true;
                var launchTarget = launchTargets.FirstOrDefault();
                if (!string.IsNullOrEmpty(launchTarget))
                {
                    var launched = await _instanceProvider.TryLaunchAsync(launchTarget, cancellationToken);
                    _logger.LogInformation(
                        launched
                            ? "Launched Visual Studio for debugger auto-attach using solution {Solution}."
                            : "Failed to launch Visual Studio for debugger auto-attach using solution {Solution}.",
                        launchTarget);
                }
            }

            if (DateTimeOffset.UtcNow + pollInterval > deadline)
            {
                break;
            }

            await _delay.DelayAsync(pollInterval, cancellationToken);
        }

        _logger.LogWarning(
            "Debugger auto-attach to plugin runtime process {ProcessId} timed out or was unavailable.",
            process.Id);
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths, bool applyPlatformNormalization)
    {
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(path => applyPlatformNormalization ? Path.GetFullPath(path) : path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IVisualStudioInstance? FindMatchingInstance(
        IReadOnlyList<IVisualStudioInstance> instances,
        IReadOnlyList<string> candidateSolutions)
    {
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance.SolutionPath))
            {
                continue;
            }

            if (candidateSolutions.Contains(instance.SolutionPath, StringComparer.OrdinalIgnoreCase))
            {
                return instance;
            }
        }

        return null;
    }
}

public interface IVisualStudioInstanceProvider
{
    IReadOnlyList<IVisualStudioInstance> GetRunningInstances();

    Task<bool> TryLaunchAsync(string solutionPath, CancellationToken cancellationToken);
}

public interface IVisualStudioInstance
{
    string? SolutionPath { get; }

    AttachResult TryAttachToProcess(int processId);
}

public enum AttachResult
{
    Attached,
    AlreadyAttached,
    ProcessNotFound,
    Failed
}

public interface IDebuggerAttachmentGuard
{
    bool IsDebuggerAttached(Process process);
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IOperatingSystemInformation
{
    bool IsWindows();

    bool IsLinux();
}

internal sealed class DefaultAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

internal sealed class OperatingSystemInformation : IOperatingSystemInformation
{
    public bool IsWindows() => OperatingSystem.IsWindows();

    public bool IsLinux() => OperatingSystem.IsLinux();
}

internal sealed class DebuggerAttachmentGuard : IDebuggerAttachmentGuard
{
    private readonly ILogger<DebuggerAttachmentGuard> _logger;

    public DebuggerAttachmentGuard(ILogger<DebuggerAttachmentGuard> logger)
    {
        _logger = logger;
    }

    public bool IsDebuggerAttached(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (CheckRemoteDebuggerPresent(process.Handle, out var isAttached))
            {
                return isAttached;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to check debugger presence for process {ProcessId}.", process.Id);
        }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(
        IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);
}

internal sealed class RotVisualStudioInstanceProvider : IVisualStudioInstanceProvider
{
    private readonly ILogger<RotVisualStudioInstanceProvider> _logger;

    public RotVisualStudioInstanceProvider(ILogger<RotVisualStudioInstanceProvider> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<IVisualStudioInstance> GetRunningInstances()
    {
        var instances = new List<IVisualStudioInstance>();
        if (!OperatingSystem.IsWindows())
        {
            return instances;
        }

        try
        {
            GetRunningObjectTable(0, out var rot);
            rot.EnumRunning(out var monikerEnum);
            var monikers = new IMoniker[1];
            while (monikerEnum.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    rot.GetObject(monikers[0], out var comObject);
                    if (comObject is DTE2 dte)
                    {
                        instances.Add(new VisualStudioInstance(dte));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect Visual Studio instance from ROT.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to enumerate Visual Studio instances from ROT.");
        }

        return instances;
    }

    public Task<bool> TryLaunchAsync(string solutionPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "devenv.exe",
                Arguments = $"\"{solutionPath}\"",
                UseShellExecute = true
            };

            var process = Process.Start(startInfo);
            return Task.FromResult(process != null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to launch Visual Studio for solution {SolutionPath}.", solutionPath);
            return Task.FromResult(false);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
}

internal sealed class VisualStudioInstance : IVisualStudioInstance
{
    private readonly DTE2 _dte;

    public VisualStudioInstance(DTE2 dte)
    {
        _dte = dte;
    }

    public string? SolutionPath
    {
        get
        {
            try
            {
                var solution = _dte.Solution?.FullName;
                return string.IsNullOrWhiteSpace(solution) ? null : Path.GetFullPath(solution);
            }
            catch
            {
                return null;
            }
        }
    }

    public AttachResult TryAttachToProcess(int processId)
    {
        try
        {
            foreach (EnvDTE.Process debuggedProcess in _dte.Debugger.DebuggedProcesses)
            {
                if (debuggedProcess.ProcessID == processId)
                {
                    return AttachResult.AlreadyAttached;
                }
            }

            foreach (Process2 process in _dte.Debugger.LocalProcesses)
            {
                if (process.ProcessID == processId)
                {
                    process.Attach();
                    return AttachResult.Attached;
                }
            }

            return AttachResult.ProcessNotFound;
        }
        catch
        {
            return AttachResult.Failed;
        }
    }
}
