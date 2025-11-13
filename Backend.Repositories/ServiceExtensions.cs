using Backend.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Repositories;
public static class ServiceExtensions
{
    public static IServiceCollection AddRelationalDataRepositories(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();

        // Register your relational data repositories here
        // e.g., services.AddScoped<IYourRepository, YourRepositoryImplementation>();
        return services;
    }
}
