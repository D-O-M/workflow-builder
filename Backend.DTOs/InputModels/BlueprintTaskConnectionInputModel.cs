using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class BlueprintTaskConnectionInputModel : IValidatableObject
{
    /// <summary>Optional: the ID of the output channel this connection represents (maps to Channels.Id).</summary>
    public int? ChannelId { get; init; }

    /// <summary>Optional: the name of the output channel this connection represents.</summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// The task instance ID of the destination task within this payload.
    /// </summary>
    [Required]
    public Guid ConnectedTaskInstanceId { get; init; }

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
    }
}
