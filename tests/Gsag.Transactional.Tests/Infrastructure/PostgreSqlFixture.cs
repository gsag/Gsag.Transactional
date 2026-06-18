using Testcontainers.PostgreSql;
using Xunit;

namespace Gsag.Transactional.Tests.Infrastructure;

public class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString => $"{_container!.GetConnectionString()};Include Error Detail=true";

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
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
}

[CollectionDefinition("PostgreSQL Collection")]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
}
