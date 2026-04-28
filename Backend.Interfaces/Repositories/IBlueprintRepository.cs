using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;

namespace Backend.Interfaces.Repositories;

public interface IBlueprintRepository
{
    Task<ExecutableBlueprint> GetFullBlueprintById(
        int id, CancellationToken cancellationToken);

    Task<int> GetLatestBlueprintVersionId(
        int blueprintDefinitionId,
        CancellationToken cancellationToken);

    Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken);

    Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken);

    Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken);

    Task<BlueprintVersionViewModel> CreateVersion(BlueprintVersionInputModel model, CancellationToken cancellationToken);
}
