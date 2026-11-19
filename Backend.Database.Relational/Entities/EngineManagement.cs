namespace Backend.Database.Relational.Entities;

public sealed class EngineFleetPolicy
{
    public int Id { get; set; }
    public long Revision { get; set; }
    public string Document { get; set; } = "{}";
}

public sealed class EngineManagementChange
{
    public long Revision { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class EnginePolicyObservation
{
    public required string EngineId { get; set; }
    public long? AppliedRevision { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? FailureCode { get; set; }
    public string ServingVersionIdsJson { get; set; } = "[]";
}
