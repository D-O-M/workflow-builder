using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;

namespace Backend.Interfaces.Repositories;

public interface IPhasedBlueprintRepository
{
    Task<List<PhasedBlueprintViewModel>> GetAllAsync(CancellationToken cancellationToken);

    Task<PhasedBlueprintViewModel> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<PhasedBlueprintVersion> GetVersionGraphAsync(int phasedBlueprintVersionId, CancellationToken cancellationToken);

    Task<int> GetLatestVersionIdAsync(int phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedBlueprintViewModel> CreateAsync(PhasedBlueprintInputModel model, CancellationToken cancellationToken);

    Task<PhasedBlueprintVersionViewModel> CreateVersionAsync(PhasedBlueprintVersionInputModel model, CancellationToken cancellationToken);
}

