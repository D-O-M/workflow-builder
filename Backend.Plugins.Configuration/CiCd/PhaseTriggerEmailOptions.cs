using System.ComponentModel;

namespace Backend.Plugins.Configuration.CiCd;

public sealed class PhaseTriggerEmailOptions
{
    [Description("Default approval recipient for generated CI/CD phase trigger emails.")]
    public string? ApprovalEmailAddress { get; init; }

    [Description("Default API base URL used when building phase trigger approval links.")]
    public Uri? TriggerBaseUrl { get; init; }

    [Description("Default local dev outbox directory for generated approval emails.")]
    public string? OutboxRoot { get; init; }
}
