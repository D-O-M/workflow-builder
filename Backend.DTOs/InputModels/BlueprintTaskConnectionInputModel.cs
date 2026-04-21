using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class BlueprintConnectionInputModel : IValidatableObject
{
    /// <summary>Optional: the ID of the output channel this connection represents (maps to Channels.Id).</summary>
    public int? ChannelId { get; init; }

    /// <summary>Optional: the name of the output channel this connection represents.</summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// The task instance ID of the destination task within this payload.
    /// </summary>
    [Required]
    public Guid ConnectedBlueprintNodeId { get; init; }

    [Range(0, 11)]
    public int Anchor { get; init; }

    [Required]
    public string Side { get; init; } = null!;

    [Range(0, int.MaxValue)]
    public int Position { get; init; }

    public List<BlueprintPropertyMappingInputModel> PropertyMappings { get; init; } = new();

    /// <summary>
    /// Enforces: at least one of ChannelId / ChannelName must be supplied.
    /// (JSON Schema "anyOf" equivalent.)
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ChannelId is null && string.IsNullOrWhiteSpace(ChannelName))
        {
            yield return new ValidationResult(
                "Either channelId or channelName must be provided.",
                new[] { nameof(ChannelId), nameof(ChannelName) });
        }

        for (var index = 0; index < PropertyMappings.Count; index++)
        {
            var mapping = PropertyMappings[index];
            if (string.IsNullOrWhiteSpace(mapping.SourceProperty))
            {
                yield return new ValidationResult(
                    "propertyMappings.sourceProperty is required.",
                    [$"{nameof(PropertyMappings)}[{index}].{nameof(BlueprintPropertyMappingInputModel.SourceProperty)}"]);
            }

            if (string.IsNullOrWhiteSpace(mapping.TargetProperty))
            {
                yield return new ValidationResult(
                    "propertyMappings.targetProperty is required.",
                    [$"{nameof(PropertyMappings)}[{index}].{nameof(BlueprintPropertyMappingInputModel.TargetProperty)}"]);
            }
        }
    }
}
