namespace Backend.Security.Authorization;

public static class WorkflowAuthorizationActions
{
    public const string BlueprintRead = "blueprint.read";
    public const string BlueprintCreate = "blueprint.create";
    public const string BlueprintUpdate = "blueprint.update";
    public const string BlueprintVersionCreate = "blueprint.version.create";
    public const string BlueprintImport = "blueprint.import";
    public const string BlueprintExport = "blueprint.export";
    public const string ProcessRead = "process.read";
    public const string ProcessStart = "process.start";
    public const string ProcessHistoryRead = "process.history.read";
    public const string StepRead = "step.read";
    public const string StepLogsRead = "step.logs.read";
    public const string StepLogsStream = "step.logs.stream";
    public const string PhasedBlueprintRead = "phased-blueprint.read";
    public const string PhasedBlueprintCreate = "phased-blueprint.create";
    public const string PhasedBlueprintPhaseTrigger = "phased-blueprint.phase.trigger";
    public const string PluginRead = "plugin.read";
    public const string PluginUse = "plugin.use";
    public const string TaskCatalogRead = "task-catalog.read";
}
