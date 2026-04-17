using Microsoft.EntityFrameworkCore;

namespace Backend.API.Infrastructure;

public interface IWorkflowDbContextOptionsConfigurator
{
    void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider);
}
