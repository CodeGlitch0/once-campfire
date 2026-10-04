using Campfire.Web.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Campfire.Web.Tests;

public sealed class SecretKeyTests
{
    [Fact]
    public void Resolve_OutsideDevelopment_RejectsMissingAndDevelopmentConstant()
    {
        var missing = new ConfigurationBuilder().Build();
        var production = new HostEnv("Production");
        Assert.Throws<InvalidOperationException>(() => SecretKey.Resolve(missing, production));

        var constant = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SECRET_KEY_BASE"] = SecretKey.Default,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => SecretKey.Resolve(constant, production));

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campfire:SecretKeyBase"] = "production-secret-not-the-constant",
        }).Build();
        Assert.Equal("production-secret-not-the-constant", SecretKey.Resolve(configured, production));
        Assert.Equal(SecretKey.Default, SecretKey.Resolve(missing, new HostEnv("Development")));
    }

    [Fact]
    public void Production_WithoutSecret_RefusesToStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campfire-secret-{Guid.NewGuid():N}.sqlite");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Production);
            builder.UseSetting("Campfire:DatabasePath", path);
            builder.UseSetting("DISABLE_SSL", "1");
        });

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("signing secret", thrown.ToString(), StringComparison.OrdinalIgnoreCase);
        TryDelete(path);
    }

    [Fact]
    public async Task Production_WithSecret_Starts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campfire-secret-{Guid.NewGuid():N}.sqlite");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Production);
            builder.UseSetting("Campfire:DatabasePath", path);
            builder.UseSetting("SECRET_KEY_BASE", "production-secret-not-the-constant");
            builder.UseSetting("DISABLE_SSL", "1");
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/up");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class HostEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Campfire";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
