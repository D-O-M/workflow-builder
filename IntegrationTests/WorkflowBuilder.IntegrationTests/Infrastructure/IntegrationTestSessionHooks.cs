namespace WorkflowBuilder.IntegrationTests.Infrastructure;

public static class IntegrationTestSessionHooks
{
    [After(TestSession)]
    public static Task DisposeSharedAspireEnvironment() =>
        SharedDistributedAppTestEnvironment.DisposeAsync();
}
