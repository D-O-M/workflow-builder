using System.Diagnostics;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly ILogger _logger;
    private readonly NanoServiceSignalsClient _signalsClient;
    private readonly List<TaskContext> _tasks = new();
    private Process? _process;

    public NanoServiceAgent(
        ILogger<NanoServiceAgent> logger,
        NanoServiceSignalsClient signalsClient)
    {
        _logger = logger;
        _signalsClient = signalsClient;
    }

    public async Task<bool> InitialiseAsync(
        FileInfo fileInfo,
        IEnumerable<NanoServiceInputChannelModel> inputChannels,
        NanoServiceGlobalStateSnapshot? globalState,
        CancellationToken cancellationToken)
    {
        _process = CreateProcess(fileInfo);
        if (!_process.Start())
        {
            return false;
        }

        await _signalsClient.EstablishAsync(_process, fileInfo.Name, inputChannels, globalState, cancellationToken);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return true;
    }

    private Process CreateProcess(FileInfo executableFileInfo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executableFileInfo.FullName,
            Arguments = "",
            WorkingDirectory = executableFileInfo.Directory!.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogInformation(
                    "[{NanoServiceName}] {Line}",
                    executableFileInfo.Name,
                    e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogInformation(
                    "[{NanoServiceName}] {Line}",
                    executableFileInfo.Name,
                    e.Data);
            }
        };

        return process;
    }

    public bool EnqueueTask(TaskContext taskContext)
    {
        _tasks.Add(taskContext);
        return true;
    }

    private void CompleteTask(TaskContext taskContext)
    {

    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.CloseMainWindow();
                    if (!_process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
                    {
                        _process.Kill(true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Swallow exceptions from querying an exited or disposed process.
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        await _signalsClient.DisposeAsync();
    }
}
