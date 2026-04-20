using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class ExportedBlueprintConnectionInputModel
{
    [Required]
    public string ChannelName { get; init; } = null!;

    [Required]
    public Guid ConnectedTaskKey { get; init; }

    [Range(0, 11)]
    public int Anchor { get; init; }

    [Required]
    public string Side { get; init; } = null!;

    [Range(0, int.MaxValue)]
    public int Position { get; init; }
}
