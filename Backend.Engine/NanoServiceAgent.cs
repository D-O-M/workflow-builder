using System.Diagnostics;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly ILogger _logger;
    private readonly NanoServiceSignalsClient _signalsClient;
    private readonly List<TaskContext> _tasks = new();

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
        var process = CreateProcess(fileInfo);
        if (!process.Start())
        {
            return false;
        }

        await _signalsClient.EstablishAsync(process, fileInfo.Name, inputChannels, globalState, cancellationToken);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return true;
    }

    private Process CreateProcess(FileInfo executableFileInfo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executableFileInfo.Name,
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
}
