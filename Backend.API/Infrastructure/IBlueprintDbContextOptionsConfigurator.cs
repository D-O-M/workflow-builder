using Microsoft.EntityFrameworkCore;

namespace Backend.API.Infrastructure;

public interface IBlueprintDbContextOptionsConfigurator
{
    void Configure(
        DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider);
}
