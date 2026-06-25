using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Gsag.Transactional.Tests.Infrastructure;

public class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString => $"{_container!.GetConnectionString()};Include Error Detail=true";

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:alpine")
            .WithDatabase("checkout_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    public async Task<string> CreateTestDatabaseAsync(string dbName)
    {
        var baseConnStr = _container!.GetConnectionString();
        using var conn = new NpgsqlConnection(baseConnStr);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(baseConnStr)
        {
            Database = dbName
        };
        return builder.ConnectionString + ";Include Error Detail=true";
    }

    public async Task DropTestDatabaseAsync(string dbName)
    {
        using var conn = new NpgsqlConnection(_container!.GetConnectionString());
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("PostgreSQL Collection")]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
}
