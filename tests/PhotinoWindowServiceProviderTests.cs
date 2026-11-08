using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
public sealed class PhotinoWindowServiceProviderTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void GetKeyedService_ResolvesKeyedServiceFromScope()
    {
        var services = new ServiceCollection();
        var primary = new Service();
        services.AddKeyedSingleton<IService>("primary", primary);
        using var rootServices = services.BuildServiceProvider();

        using var windowServices = new PhotinoWindowServiceProvider(rootServices, new PhotinoWindowResourceHandler());

        Assert.AreSame(primary, windowServices.GetKeyedService<IService>("primary"));
        Assert.AreSame(primary, windowServices.GetRequiredKeyedService<IService>("primary"));
        Assert.IsNull(windowServices.GetKeyedService<IService>("missing"));
    }

    [TestMethod]
    public void GetKeyedService_NestedScopeSupportsKeyedServices()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IService, Service>("scoped");
        using var rootServices = services.BuildServiceProvider();

        using var windowServices = new PhotinoWindowServiceProvider(rootServices, new PhotinoWindowResourceHandler());
        using var scope = windowServices.CreateScope();

        Assert.IsInstanceOfType<IKeyedServiceProvider>(scope.ServiceProvider);
        Assert.IsNotNull(scope.ServiceProvider.GetKeyedService<IService>("scoped"));
    }

    [TestMethod]
    public void GetKeyedService_NullKeyUsesWindowOverrides()
    {
        using var rootServices = new ServiceCollection().BuildServiceProvider();
        using var windowServices = new PhotinoWindowServiceProvider(rootServices, new PhotinoWindowResourceHandler());

        Assert.AreSame(windowServices, windowServices.GetKeyedService(typeof(IServiceProvider), null));
    }

    [TestMethod]
    public async Task HttpClient_ConstructorInjectedIntoServiceUsesWindowResources()
    {
        var resourceHandler = new StubResourceHandler("app://localhost/data.json", "{\"value\":42}");
        using var rootServices = CreateBlazorServices();
        using var windowServices = new PhotinoWindowServiceProvider(rootServices, resourceHandler);

        var service = windowServices.GetRequiredService<HttpConsumer>();

        Assert.AreEqual(new Uri("app://localhost/"), service.Http.BaseAddress);
        Assert.AreEqual("{\"value\":42}", await service.Http.GetStringAsync("data.json").WaitAsync(s_timeout));
        Assert.AreSame(resourceHandler, windowServices.GetRequiredService<IPhotinoWebResourceHandler>());
    }

    [TestMethod]
    public void HttpClient_NestedScopeIsBoundToSameWindow()
    {
        var resourceHandler = new StubResourceHandler("app://localhost/data.json", "{}");
        using var rootServices = CreateBlazorServices();
        using var windowServices = new PhotinoWindowServiceProvider(rootServices, resourceHandler);
        using var scope = windowServices.CreateScope();

        Assert.AreSame(resourceHandler, scope.ServiceProvider.GetRequiredService<IPhotinoWebResourceHandler>());
        Assert.AreNotSame(
            windowServices.GetRequiredService<HttpClient>(),
            scope.ServiceProvider.GetRequiredService<HttpClient>());
    }

    [TestMethod]
    public void HttpClient_UserRegistrationTakesPrecedence()
    {
        var services = CreateBlazorServiceCollection();
        var userClient = new HttpClient { BaseAddress = new Uri("https://api.example.com/") };
        services.AddSingleton(userClient);
        using var rootServices = services.BuildServiceProvider();
        using var windowServices = new PhotinoWindowServiceProvider(rootServices, new PhotinoWindowResourceHandler());

        Assert.AreSame(userClient, windowServices.GetRequiredService<HttpClient>());
    }

    private static ServiceCollection CreateBlazorServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBlazorDesktop();
        services.AddScoped<HttpConsumer>();
        return services;
    }

    private static ServiceProvider CreateBlazorServices()
    {
        return CreateBlazorServiceCollection().BuildServiceProvider();
    }

    private interface IService;

    private sealed class Service : IService;

    private sealed class HttpConsumer(HttpClient http)
    {
        public HttpClient Http { get; } = http;
    }

    private sealed class StubResourceHandler(string url, string content) : IPhotinoWebResourceHandler
    {
        public Stream? HandleWebRequest(string requestUrl, out string? contentType)
        {
            if (requestUrl == url)
            {
                contentType = "application/json";
                return new MemoryStream(Encoding.UTF8.GetBytes(content));
            }

            contentType = null;
            return null;
        }
    }
}
