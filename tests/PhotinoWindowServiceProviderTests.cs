using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
public sealed class PhotinoWindowServiceProviderTests
{
    private static readonly Uri s_appBaseUri = new("app://localhost/");

    [TestMethod]
    public void GetKeyedService_ResolvesKeyedServiceFromScope()
    {
        var services = new ServiceCollection();
        var primary = new Service();
        services.AddKeyedSingleton<IService>("primary", primary);
        using var rootServices = services.BuildServiceProvider();

        using var windowServices = CreateWindowServices(rootServices);

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

        using var windowServices = CreateWindowServices(rootServices);
        using var scope = windowServices.CreateScope();

        Assert.IsInstanceOfType<IKeyedServiceProvider>(scope.ServiceProvider);
        Assert.IsNotNull(scope.ServiceProvider.GetKeyedService<IService>("scoped"));
    }

    [TestMethod]
    public void GetKeyedService_NullKeyUsesWindowOverrides()
    {
        using var rootServices = new ServiceCollection().BuildServiceProvider();
        using var windowServices = CreateWindowServices(rootServices);

        Assert.AreSame(windowServices, windowServices.GetKeyedService(typeof(IServiceProvider), null));
    }

    private static PhotinoWindowServiceProvider CreateWindowServices(IServiceProvider rootServices)
    {
        return new PhotinoWindowServiceProvider(rootServices, new PhotinoWindowResourceHandler(), s_appBaseUri);
    }

    private interface IService;

    private sealed class Service : IService;
}
