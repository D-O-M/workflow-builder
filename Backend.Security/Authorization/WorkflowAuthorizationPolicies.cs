namespace Backend.Security.Authorization;

public static class WorkflowAuthorizationPolicies
{
    public const string Authenticated = "WorkflowBuilderAuthenticated";
    public const string BlueprintAuthor = nameof(BlueprintAuthor);
    public const string ProcessOperator = nameof(ProcessOperator);
    public const string RuntimeEngineer = nameof(RuntimeEngineer);
    public const string Auditor = nameof(Auditor);
    public const string Admin = nameof(Admin);
}
