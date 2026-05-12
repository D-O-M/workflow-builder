using System.Diagnostics;
using System.Text;

namespace CiCd.Common;

public sealed record CommandRunResult(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string LogPath,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public static class CommandRunner
{
    public static async Task<CommandRunResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var argumentList = arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)).ToArray();
        var startedAt = DateTimeOffset.UtcNow;
        await using var writer = new StreamWriter(logPath, append: File.Exists(logPath), new UTF8Encoding(false));
        await writer.WriteLineAsync($"$ {fileName} {string.Join(' ', argumentList.Select(QuoteForLog))}");
        await writer.WriteLineAsync($"# WorkingDirectory: {workingDirectory}");
        await writer.WriteLineAsync($"# StartedAt: {startedAt:O}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in argumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start command '{fileName}'.");
        }

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation is already underway; command failure will be reported by WaitForExitAsync.
            }
        });

        var writeGate = new SemaphoreSlim(1, 1);
        var outputTask = PipeAsync(process.StandardOutput, "out", writer, writeGate, cancellationToken);
        var errorTask = PipeAsync(process.StandardError, "err", writer, writeGate, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);

        var completedAt = DateTimeOffset.UtcNow;
        await writer.WriteLineAsync($"# CompletedAt: {completedAt:O}");
        await writer.WriteLineAsync($"# ExitCode: {process.ExitCode}");

        var result = new CommandRunResult(
            fileName,
            argumentList,
            workingDirectory,
            logPath,
            process.ExitCode,
            startedAt,
            completedAt);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command '{fileName}' failed with exit code {result.ExitCode}. See log: {logPath}");
        }

        return result;
    }

    public static async Task<string> CaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)).ToArray();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in argumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command '{fileName}' failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output.Trim();
    }

    public static string ResolveExecutable(string executableName) =>
        OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? executableName
                : $"{executableName}.cmd"
            : executableName;

    public static string ResolvePowerShellExecutable() =>
        OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";

    private static async Task PipeAsync(
        TextReader reader,
        string prefix,
        StreamWriter writer,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            await writeGate.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteLineAsync($"[{prefix}] {line}");
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                writeGate.Release();
            }
        }
    }

    private static string QuoteForLog(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;
}
