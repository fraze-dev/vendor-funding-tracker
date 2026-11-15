using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.Azurite;
using Testcontainers.MsSql;

namespace VendorFunding.Api.Tests;

/// <summary>Runs the real API against a throwaway SQL Server container (requires Docker).</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:VendorFunding", _sql.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:SeedDemoData", "false");
    }

    public virtual Task InitializeAsync() => _sql.StartAsync();

    public new virtual async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
    }
}

/// <summary>Adds Azurite, the Blob Storage emulator, for tests that upload sales files.</summary>
public sealed class ApiWithStorageFactory : ApiFactory
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        // The Storage SDK can be newer than the emulator; the API surface we use is unchanged.
        .WithCommand("--skipApiVersionCheck")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("SalesStorage:ConnectionString", _azurite.GetConnectionString());
    }

    public override async Task InitializeAsync()
    {
        await Task.WhenAll(base.InitializeAsync(), _azurite.StartAsync());
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _azurite.DisposeAsync();
    }
}
