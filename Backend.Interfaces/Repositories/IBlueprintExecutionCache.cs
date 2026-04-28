using Backend.Models;

namespace Backend.Interfaces.Repositories;

public interface IBlueprintExecutionCache
{
    Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken);
}
