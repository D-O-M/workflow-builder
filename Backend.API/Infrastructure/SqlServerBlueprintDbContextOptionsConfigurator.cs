using Microsoft.EntityFrameworkCore;

namespace Backend.API.Infrastructure;

public sealed class SqlServerBlueprintDbContextOptionsConfigurator : IBlueprintDbContextOptionsConfigurator
{
    public void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
        var tasksDbConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
            configuration,
            environment);

        optionsBuilder.UseSqlServer(tasksDbConnectionString);
    }
}
