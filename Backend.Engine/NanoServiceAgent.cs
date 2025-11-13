using System.Diagnostics;

namespace Backend.Engine;

public class NanoServiceAgent
{
    private readonly ILogger _logger;

    public NanoServiceAgent(ILogger<NanoServiceAgent> logger)
    {
        _logger = logger;
    }

    public bool Initialise(FileInfo fileInfo)
    {
        var process = CreateProcess(fileInfo);
        return process.Start();
    }

    private Process CreateProcess(FileInfo executableFileInfo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executableFileInfo.Name,
            Arguments = "",
            WorkingDirectory = executableFileInfo.Directory!.Name,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
}
