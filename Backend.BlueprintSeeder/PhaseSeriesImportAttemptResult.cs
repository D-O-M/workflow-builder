namespace Backend.BlueprintSeeder;

internal sealed class PhaseSeriesImportAttemptResult
{
    private PhaseSeriesImportAttemptResult()
    {
    }

    public bool Succeeded { get; private init; }
    public string PhaseSeriesKey { get; private init; } = string.Empty;
    public int? DefinitionId { get; private init; }
    public string Status { get; private init; } = string.Empty;
    public int StatusCode { get; private init; }
    public string ResponseBody { get; private init; } = string.Empty;
    public bool Retryable { get; private init; }

    public static PhaseSeriesImportAttemptResult Success(string key, int definitionId, string status) =>
        new()
        {
            Succeeded = true,
            PhaseSeriesKey = key,
            DefinitionId = definitionId,
            Status = status
        };

    public static PhaseSeriesImportAttemptResult Failure(
        int statusCode,
        string responseBody,
        bool retryable) =>
        new()
        {
            Succeeded = false,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            Retryable = retryable
        };
}
