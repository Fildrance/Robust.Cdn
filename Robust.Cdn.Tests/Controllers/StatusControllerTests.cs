using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Robust.Cdn.Tests.Controllers;

/// <summary>
/// Integration tests for <c>StatusController</c>.
///
/// Endpoint: GET /control/status
/// </summary>
[Trait("Category", "Integration")]
public sealed class StatusControllerTests(WebApplicationFactory<Program> factory, DatabaseFixture database)
    : TestBase(factory, database)
{
    protected override Dictionary<string, string?> GetConfigurationOverrides()
    {
        var baseDir = Database.CreateTestVersionOnDisk("testfork", "1.0.0");
        return new()
        {
            ["Manifest:FileDiskPath"] = baseDir,
        };
    }

    [Fact]
    public async Task GetControlStatus_ReturnsOkWithVersionInfo()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/control/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK", content);
        Assert.Contains("contentVersions", content);
        Assert.Contains("version", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetControlStatus_WithExistingVersion_ReturnsNonZeroCount()
    {
        var client = Factory.CreateClient();

        // The ingest job runs asynchronously at startup. Poll until we see some version info uploaded.
        const int maxAttempts = 20;
        for (var i = 0; i < maxAttempts; i++)
        {
            var response = await client.GetAsync("/control/status");
            var content = await response.Content.ReadAsStringAsync();
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, Object>>(content);
            if (deserialized != null
                && int.TryParse(deserialized["contentVersions"].ToString(), out var versionsCount)
                && versionsCount > 0)
                return;

            await Task.Delay(200);
        }

        Assert.Fail("Version was not ingested after startup");
    }
}
