using System.Collections.Concurrent;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class InMemoryWorkflowInstanceRepository
    : IWorkflowInstanceRepository<Dictionary<string, JsonElement>>
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;
    private readonly ConcurrentDictionary<(string WorkflowId, string BlueprintId), WorkflowObject<Dictionary<string, JsonElement>>> _workflows = new();
    private readonly string _stateRootPath;

    public InMemoryWorkflowInstanceRepository(string stateRootPath)
    {
        _stateRootPath = stateRootPath;
    }

    public Task<WorkflowObject<Dictionary<string, JsonElement>>> CreateWorkflowState(
        WorkflowObject<Dictionary<string, JsonElement>> state,
        CancellationToken cancellationToken)
    {
        var clone = Clone(state);
        var key = CreateKey(clone.Id, clone.BlueprintId);

        if (!_workflows.TryAdd(key, clone))
        {
            throw new InvalidOperationException($"Workflow instance '{clone.Id}' already exists.");
        }

        WriteSnapshot(clone);
        return Task.FromResult(Clone(clone));
    }

    public Task<WorkflowObject<Dictionary<string, JsonElement>>> GetWorkflowStateById(
        Guid workflowInstanceId,
        int workflowBlueprintId,
        CancellationToken cancellationToken)
    {
        var key = CreateKey(workflowInstanceId.ToString(), workflowBlueprintId.ToString());
        if (!_workflows.TryGetValue(key, out var workflow))
        {
            throw new EntityNotFoundException<WorkflowObject<Dictionary<string, JsonElement>>>(workflowInstanceId.ToString());
        }

        return Task.FromResult(Clone(workflow));
    }

    public Task<List<WorkflowObject<Dictionary<string, JsonElement>>>> GetWorkflowStatesForBlueprint(
        int workflowBlueprintId,
        CancellationToken cancellationToken)
    {
        var workflows = _workflows.Values
            .Where(workflow => workflow.BlueprintId == workflowBlueprintId.ToString())
            .OrderByDescending(workflow => workflow.CreatedAt)
            .Select(Clone)
            .ToList();

        return Task.FromResult(workflows);
    }

    public Task<WorkflowObject<Dictionary<string, JsonElement>>> UpdateWorkflowState(
        WorkflowObject<Dictionary<string, JsonElement>> state,
        CancellationToken cancellationToken)
    {
        var clone = Clone(state);
        var key = CreateKey(clone.Id, clone.BlueprintId);
        _workflows[key] = clone;
        WriteSnapshot(clone);
        return Task.FromResult(Clone(clone));
    }

    public WorkflowObject<Dictionary<string, JsonElement>> GetWorkflow(Guid workflowInstanceId, int blueprintVersionId)
    {
        var key = CreateKey(workflowInstanceId.ToString(), blueprintVersionId.ToString());
        if (!_workflows.TryGetValue(key, out var workflow))
        {
            throw new EntityNotFoundException<WorkflowObject<Dictionary<string, JsonElement>>>(workflowInstanceId.ToString());
        }

        return Clone(workflow);
    }

    private static (string WorkflowId, string BlueprintId) CreateKey(string workflowId, string blueprintId) =>
        (workflowId, blueprintId);

    private static WorkflowObject<Dictionary<string, JsonElement>> Clone(
        WorkflowObject<Dictionary<string, JsonElement>> workflow) =>
        JsonSerializer.Deserialize<WorkflowObject<Dictionary<string, JsonElement>>>(
            JsonSerializer.Serialize(workflow, SerializerOptions),
            SerializerOptions)
        ?? throw new InvalidOperationException("Failed to clone workflow state.");

    private void WriteSnapshot(WorkflowObject<Dictionary<string, JsonElement>> workflow)
    {
        var directory = Path.Combine(_stateRootPath, "workflows", workflow.BlueprintId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{workflow.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(workflow, SerializerOptions));
    }
}
