using Microsoft.AspNetCore.Mvc.Testing;

namespace Robust.Cdn.Tests;

/// <summary>
/// Base class for integration tests providing a pre-configured <c>WebApplicationFactory</c>
/// and automatic cleanup of temp database files after the test collection finishes.
/// </summary>
[Collection("ControllerTests")]
public abstract class TestBase : IClassFixture<DatabaseFixture>
{
    protected WebApplicationFactory<Program> Factory { get; }
    protected DatabaseFixture Database { get; }

    protected TestBase(WebApplicationFactory<Program> factory, DatabaseFixture database)
    {
        Database = database;

        var config = new Dictionary<string, string?>(GetDefaultConfiguration());
        foreach (var (key, value) in GetConfigurationOverrides())
        {
            config[key] = value;
        }

        Factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(config);
            });
        });
    }
    /// <summary>
    /// Provides default configuration for all tests.
    /// Override to change defaults globally.
    /// </summary>
    private Dictionary<string, string?> GetDefaultConfiguration()
    {
        return new()
        {
            ["Cdn:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:DatabaseFileName"] = Database.CreateTempDb(),
            ["Manifest:FileDiskPath"] = Path.GetTempPath(),
            ["Manifest:Forks:testfork:ClientZipName"] = "test",
            ["Manifest:Forks:testfork:BuildsPageLinkText"] = "test",
        };
    }

    /// <summary>
    /// Override to add or replace specific configuration keys for a particular test class.
    /// Returned values take precedence over <c>GetDefaultConfiguration()</c>.
    /// Please do not use <see cref="Factory"/> there, as this method is used
    /// in c-tor or base class and factory won't be set up yet (config is required for factory).
    /// </summary>
    protected virtual Dictionary<string, string?> GetConfigurationOverrides() => new();
}

