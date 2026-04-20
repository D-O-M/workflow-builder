using System;
using System.Collections.Generic;
using System.Text;

namespace Backend.Models;

public sealed class WTask
{
    // Node identity (BlueprintNode.Id)
    public Guid BlueprintNodeId { get; init; }

    // Versioned task identity (TaskVersion)
    public int TaskVersionId { get; init; }
    public int TaskDefinitionId { get; init; }
    public string TaskKey { get; init; } = null!;
    public int TaskVersion { get; init; }
    public string TaskName { get; init; } = null!;
    public string Path { get; init; } = null!;

    /// <summary>
    /// Your “starting task output channels (list)” requirement.
    /// Each output channel contains connections to next node(s).
    /// </summary>
    //public List<TaskOutputChannelDto> OutputChannels { get; init; } = new();

    /// <summary>
    /// Optional convenience: if you still want the input schema/criteria.
    /// If your model enforces exactly one input channel, keep it singular.
    /// </summary>
    //public TaskInputChannelDto? InputChannel { get; init; }
}
