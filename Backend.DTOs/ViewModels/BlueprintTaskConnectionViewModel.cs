namespace Backend.DTOs.ViewModels;

public sealed class BlueprintTaskConnectionViewModel
{
    /// <summary>Resolved output channel id (Channels.Id), if known.</summary>
    public int? ChannelId { get; init; }

    /// <summary>Resolved output channel name, if known.</summary>
    public string? ChannelName { get; init; }

    public Guid TaskInstanceId { get; init; }
}
