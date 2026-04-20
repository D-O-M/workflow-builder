using System;
using System.Threading.Tasks;
using Backend.Database.Relational;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Tests.TestSupport;

internal sealed class SqliteBlueprintDb : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteBlueprintDb(SqliteConnection connection, BlueprintDbContext context)
    {
        _connection = connection;
        Context = context;
    }

    public BlueprintDbContext Context { get; }

    public static async Task<SqliteBlueprintDb> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<BlueprintDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new BlueprintDbContext(options);
        await context.Database.EnsureCreatedAsync();

        return new SqliteBlueprintDb(connection, context);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
