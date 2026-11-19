namespace Backend.API.Infrastructure;

public sealed class ConditionalBranchAuthoringOptions
{
    public const string SectionName = "Features:ConditionalBranchAuthoring";
    public bool Enabled { get; init; }
}
