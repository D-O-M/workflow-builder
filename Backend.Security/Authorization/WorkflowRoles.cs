namespace Backend.Security.Authorization;

public static class WorkflowRoles
{
    public const string Admin = "workflowbuilder.admin";
    public const string BlueprintAuthor = "workflowbuilder.blueprint_author";
    public const string ProcessOperator = "workflowbuilder.process_operator";
    public const string RuntimeEngineer = "workflowbuilder.runtime_engineer";
    public const string Auditor = "workflowbuilder.auditor";
    public const string InternalPublisher = "workflowbuilder.internal_publisher";

    public static readonly string[] DevelopmentRoles =
    [
        Admin,
        BlueprintAuthor,
        ProcessOperator,
        RuntimeEngineer,
        Auditor,
        InternalPublisher
    ];
}
