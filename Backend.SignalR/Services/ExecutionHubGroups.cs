namespace Backend.SignalR.Services;

public static class ExecutionHubGroups
{
    private const string BlueprintPrefix = "blueprint:";
    private const string WorkflowPrefix = "workflow:";

    public static string Blueprint(int blueprintDefinitionId) => $"{BlueprintPrefix}{blueprintDefinitionId}";

    public static string Workflow(Guid workflowInstanceId) => $"{WorkflowPrefix}{workflowInstanceId:D}";

    public static bool TryParseBlueprint(string groupName, out int blueprintDefinitionId)
    {
        blueprintDefinitionId = default;
        return groupName.StartsWith(BlueprintPrefix, StringComparison.Ordinal) &&
            int.TryParse(groupName[BlueprintPrefix.Length..], out blueprintDefinitionId);
    }

    public static bool TryParseWorkflow(string groupName, out Guid workflowInstanceId)
    {
        workflowInstanceId = default;
        return groupName.StartsWith(WorkflowPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(groupName[WorkflowPrefix.Length..], out workflowInstanceId);
    }
}
