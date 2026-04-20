using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Backend.Database.Relational; // Adjust to your namespace

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BlueprintDbContext>
{
    public BlueprintDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<DesignTimeDbContextFactory>(optional: false)
            .Build();

        var connectionString = configuration.GetConnectionString("SqlServer");

        var optionsBuilder = new DbContextOptionsBuilder<BlueprintDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new BlueprintDbContext(optionsBuilder.Options);
    }
}
