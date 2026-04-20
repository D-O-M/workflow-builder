using System.Net.Http.Json;
using System.Text.Json;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintImportClient
{
    private static readonly HashSet<string> RetryableCodes =
    [
        BlueprintImportErrorCodes.TaskDefinitionMissing,
        BlueprintImportErrorCodes.TaskLaneMissing
    ];

    private readonly HttpClient _httpClient;

    public BlueprintImportClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<BlueprintImportAttemptResult> ImportAsync(
        ExportedBlueprintInputModel payload,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/blueprints/import",
            payload,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var viewModel = await response.Content.ReadFromJsonAsync<BlueprintImportViewModel>(cancellationToken);
            if (viewModel is null)
            {
                throw new InvalidOperationException("Blueprint import response payload was empty.");
            }

            return BlueprintImportAttemptResult.Success(viewModel);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var codes = ExtractErrorCodes(body);
        return BlueprintImportAttemptResult.Failure(
            (int)response.StatusCode,
            body,
            codes,
            codes.Count > 0 && codes.All(code => RetryableCodes.Contains(code)));
    }

    private static IReadOnlyList<string> ExtractErrorCodes(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("errors", out var errorsElement) ||
                errorsElement.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            return errorsElement.EnumerateArray()
                .Where(element => element.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                .Select(element => element.GetProperty("code").GetString())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
