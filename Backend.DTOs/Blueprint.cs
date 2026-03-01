using System;
using System.Collections.Generic;
using System.Text;

namespace Backend.DTOs;

public sealed class Blueprint
{
    // Blueprint version identity
    public int BlueprintVersionId { get; init; }
    public int BlueprintDefinitionId { get; init; }
    public string BlueprintKey { get; init; } = null!;
    public int Version { get; init; }
    public string Name { get; init; } = null!;

    /// <summary>
    /// Entry point for linked traversal.
    /// </summary>
    //public TaskNodeDto StartingTask { get; init; } = null!;

    /// <summary>
    /// Optional: an index of all tasks in this blueprint version by node id.
    /// Use this to prevent duplication and/or detect cycles.
    /// </summary>
    //public Dictionary<Guid, TaskNodeDto> TasksById { get; init; } = new();
}
