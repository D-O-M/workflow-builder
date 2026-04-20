namespace Backend.SignalR.Services;

public static class ProcessHubGroups
{
    private const string BlueprintPrefix = "blueprint:";
    private const string ProcessPrefix = "process:";

    public static string Blueprint(int blueprintDefinitionId) => $"{BlueprintPrefix}{blueprintDefinitionId}";

    public static string Process(Guid processId) => $"{ProcessPrefix}{processId:D}";

    public static bool TryParseBlueprint(string groupName, out int blueprintDefinitionId)
    {
        blueprintDefinitionId = default;
        return groupName.StartsWith(BlueprintPrefix, StringComparison.Ordinal) &&
            int.TryParse(groupName[BlueprintPrefix.Length..], out blueprintDefinitionId);
    }

    public static bool TryParseProcess(string groupName, out Guid processId)
    {
        processId = default;
        return groupName.StartsWith(ProcessPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(groupName[ProcessPrefix.Length..], out processId);
    }
}
