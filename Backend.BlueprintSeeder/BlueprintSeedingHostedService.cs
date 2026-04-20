using System.Text.Json;
using Backend.Models.InputModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintSeedingHostedService : BackgroundService
{
    private const int MaxRetryAttempts = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlueprintImportClient _importClient;
    private readonly BlueprintSeedingOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<BlueprintSeedingHostedService> _logger;

    public BlueprintSeedingHostedService(
        BlueprintImportClient importClient,
        IOptions<BlueprintSeedingOptions> options,
        IHostApplicationLifetime applicationLifetime,
        ILogger<BlueprintSeedingHostedService> logger)
    {
        _importClient = importClient;
        _options = options.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Blueprint seeding is disabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.BlueprintsRoot))
            {
                _logger.LogWarning("Blueprint seeding root is not configured.");
                return;
            }

            var blueprintsRoot = Path.GetFullPath(_options.BlueprintsRoot);
            if (!Directory.Exists(blueprintsRoot))
            {
                _logger.LogWarning(
                    "Blueprint seeding root {BlueprintsRoot} does not exist; skipping seeding.",
                    blueprintsRoot);
                return;
            }

            var files = Directory.EnumerateFiles(blueprintsRoot, "*.blueprint.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                _logger.LogInformation(
                    "Blueprint seeding found no blueprint files under {BlueprintsRoot}.",
                    blueprintsRoot);
                return;
            }

            _logger.LogInformation(
                "Starting blueprint seeding from {BlueprintsRoot}.",
                blueprintsRoot);

            var importedCount = 0;
            var failedCount = 0;
            var skippedInvalidCount = 0;

            foreach (var filePath in files)
            {
                stoppingToken.ThrowIfCancellationRequested();

                ExportedBlueprintInputModel? payload;
                try
                {
                    await using var stream = File.OpenRead(filePath);
                    payload = await JsonSerializer.DeserializeAsync<ExportedBlueprintInputModel>(
                        stream,
                        JsonOptions,
                        stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    skippedInvalidCount++;
                    _logger.LogWarning(ex, "Skipping invalid blueprint seed file {FilePath}.", filePath);
                    continue;
                }

                if (payload is null)
                {
                    skippedInvalidCount++;
                    _logger.LogWarning("Skipping blueprint seed file {FilePath} because it was empty.", filePath);
                    continue;
                }

                var imported = await ImportWithRetriesAsync(filePath, payload, stoppingToken);
                if (imported)
                {
                    importedCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            _logger.LogInformation(
                "Blueprint seeding finished. Imported: {ImportedCount}, invalid: {SkippedInvalidCount}, failed: {FailedCount}.",
                importedCount,
                skippedInvalidCount,
                failedCount);
        }
        finally
        {
            _applicationLifetime.StopApplication();
        }
    }

    private async Task<bool> ImportWithRetriesAsync(
        string filePath,
        ExportedBlueprintInputModel payload,
        CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            stoppingToken.ThrowIfCancellationRequested();

            BlueprintImportAttemptResult result;
            try
            {
                result = await _importClient.ImportAsync(payload, stoppingToken);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxRetryAttempts)
                {
                    _logger.LogWarning(ex, "Failed to import blueprint seed {FilePath}.", filePath);
                    return false;
                }

                _logger.LogInformation(
                    ex,
                    "Blueprint seed {FilePath} could not reach the API on attempt {Attempt}/{MaxAttempts}; retrying in {DelaySeconds}s.",
                    filePath,
                    attempt,
                    MaxRetryAttempts,
                    RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, stoppingToken);
                continue;
            }

            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Imported blueprint seed {FilePath} with status {Status} for key {BlueprintKey}.",
                    filePath,
                    result.ViewModel!.Status,
                    result.ViewModel.BlueprintKey);
                return true;
            }

            if (!result.Retryable || attempt == MaxRetryAttempts)
            {
                _logger.LogWarning(
                    "Failed to import blueprint seed {FilePath}. StatusCode: {StatusCode}. ErrorCodes: {ErrorCodes}. Response: {ResponseBody}",
                    filePath,
                    result.StatusCode,
                    result.ErrorCodes.Count == 0 ? "<none>" : string.Join(", ", result.ErrorCodes),
                    result.ResponseBody);
                return false;
            }

            _logger.LogInformation(
                "Blueprint seed {FilePath} hit retryable import errors ({ErrorCodes}) on attempt {Attempt}/{MaxAttempts}; retrying in {DelaySeconds}s.",
                filePath,
                string.Join(", ", result.ErrorCodes),
                attempt,
                MaxRetryAttempts,
                RetryDelay.TotalSeconds);
            await Task.Delay(RetryDelay, stoppingToken);
        }

        return false;
    }
}
