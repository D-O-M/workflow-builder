using System.Collections.Concurrent;
using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class InMemoryTaskInstanceRepository : ITaskInstanceRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;
    private readonly ConcurrentDictionary<(string WorkflowId, string TaskId), TaskObject> _tasks = new();
    private readonly string _stateRootPath;

    public InMemoryTaskInstanceRepository(string stateRootPath)
    {
        _stateRootPath = stateRootPath;
    }

    public Task<TaskObject> CreateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken)
    {
        var clone = Clone(state);
        var key = CreateKey(clone.Id, clone.WorkflowInstanceId);

        if (!_tasks.TryAdd(key, clone))
        {
            throw new InvalidOperationException($"Task instance '{clone.Id}' already exists.");
        }

        WriteSnapshot(clone);
        return Task.FromResult(Clone(clone));
    }

    public Task<TaskObject> GetTaskInstanceById(
        Guid taskInstanceId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var key = CreateKey(taskInstanceId.ToString(), workflowInstanceId.ToString());
        if (!_tasks.TryGetValue(key, out var task))
        {
            throw new EntityNotFoundException<TaskObject>(taskInstanceId.ToString());
        }

        return Task.FromResult(Clone(task));
    }

    public Task<List<TaskObject>> GetTaskInstancesForWorkflow(
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var tasks = _tasks.Values
            .Where(task => task.WorkflowInstanceId == workflowInstanceId.ToString())
            .Select(Clone)
            .ToList();

        return Task.FromResult(tasks);
    }

    public Task<TaskObject> UpdateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken)
    {
        var clone = Clone(state);
        var key = CreateKey(clone.Id, clone.WorkflowInstanceId);
        _tasks[key] = clone;
        WriteSnapshot(clone);
        return Task.FromResult(Clone(clone));
    }

    public IReadOnlyList<TaskObject> GetTasksForWorkflow(Guid workflowInstanceId)
    {
        return _tasks.Values
            .Where(task => task.WorkflowInstanceId == workflowInstanceId.ToString())
            .Select(Clone)
            .ToList();
    }

    private static (string WorkflowId, string TaskId) CreateKey(string taskId, string workflowId) =>
        (workflowId, taskId);

    private static TaskObject Clone(TaskObject task) =>
        JsonSerializer.Deserialize<TaskObject>(
            JsonSerializer.Serialize(task, SerializerOptions),
            SerializerOptions)
        ?? throw new InvalidOperationException("Failed to clone task state.");

    private void WriteSnapshot(TaskObject task)
    {
        var directory = Path.Combine(_stateRootPath, "tasks", task.WorkflowInstanceId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{task.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(task, SerializerOptions));
    }
}
