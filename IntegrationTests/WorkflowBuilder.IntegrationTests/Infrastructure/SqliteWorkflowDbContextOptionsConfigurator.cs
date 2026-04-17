using Backend.API.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class SqliteWorkflowDbContextOptionsConfigurator : IWorkflowDbContextOptionsConfigurator
{
    private readonly string _connectionString;

    public SqliteWorkflowDbContextOptionsConfigurator(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        optionsBuilder.UseSqlite(_connectionString);
    }
}
