using Backend.Models.ViewModels;

namespace Backend.BlueprintSeeder;

internal sealed class BlueprintImportAttemptResult
{
    private BlueprintImportAttemptResult()
    {
    }

    public bool Succeeded { get; private init; }
    public BlueprintImportViewModel? ViewModel { get; private init; }
    public int StatusCode { get; private init; }
    public string ResponseBody { get; private init; } = string.Empty;
    public IReadOnlyList<string> ErrorCodes { get; private init; } = [];
    public bool Retryable { get; private init; }

    public static BlueprintImportAttemptResult Success(BlueprintImportViewModel viewModel) =>
        new()
        {
            Succeeded = true,
            ViewModel = viewModel
        };

    public static BlueprintImportAttemptResult Failure(
        int statusCode,
        string responseBody,
        IReadOnlyList<string> errorCodes,
        bool retryable) =>
        new()
        {
            Succeeded = false,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            ErrorCodes = errorCodes,
            Retryable = retryable
        };
}
