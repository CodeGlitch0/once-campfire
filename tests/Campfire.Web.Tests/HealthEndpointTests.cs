using Microsoft.AspNetCore.Mvc.Testing;

namespace Campfire.Web.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetUp_Returns200()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/up");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
