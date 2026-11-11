using Microsoft.Extensions.DependencyInjection;

namespace Photino.Blazor;

// Service provider of a Photino Blazor window. Every scope it creates is bound to the window through
// PhotinoWindowContext, so scoped registrations such as HttpClient and IPhotinoWebResourceHandler
// resolve the window's resources wherever they are injected.
internal sealed class PhotinoWindowServiceProvider : IKeyedServiceProvider, IServiceScopeFactory, IServiceScope, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceScope _scope;
    private readonly IPhotinoWebResourceHandler _resourceHandler;

    internal PhotinoWindowServiceProvider(IServiceProvider rootServices, IPhotinoWebResourceHandler resourceHandler)
        : this(
            (rootServices ?? throw new ArgumentNullException(nameof(rootServices))).GetRequiredService<IServiceScopeFactory>(),
            resourceHandler)
    {
    }

    private PhotinoWindowServiceProvider(IServiceScopeFactory scopeFactory, IPhotinoWebResourceHandler resourceHandler)
    {
        ArgumentNullException.ThrowIfNull(resourceHandler);

        _scopeFactory = scopeFactory;
        _resourceHandler = resourceHandler;
        _scope = scopeFactory.CreateScope();

        try
        {
            _scope.ServiceProvider.GetService<PhotinoWindowContext>()?.Bind(resourceHandler);
        }
        catch
        {
            _scope.Dispose();
            throw;
        }
    }

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType)
    {
        // Nested scopes must be created through this provider so they are bound to the window as well.
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceScopeFactory))
            return this;

        return _scope.ServiceProvider.GetService(serviceType);
    }

    // Blazor resolves [Inject(Key = ...)] properties only through IKeyedServiceProvider,
    // so keyed lookups must be forwarded to the wrapped scope.
    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceKey is null)
            return GetService(serviceType);

        return GetKeyedServiceProvider().GetKeyedService(serviceType, serviceKey);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceKey is null)
            return this.GetRequiredService(serviceType);

        return GetKeyedServiceProvider().GetRequiredKeyedService(serviceType, serviceKey);
    }

    private IKeyedServiceProvider GetKeyedServiceProvider()
    {
        return _scope.ServiceProvider as IKeyedServiceProvider
            ?? throw new InvalidOperationException("The application's service provider does not support keyed services.");
    }

    public IServiceScope CreateScope()
    {
        return new PhotinoWindowServiceProvider(_scopeFactory, _resourceHandler);
    }

    public void Dispose()
    {
        _scope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else
            _scope.Dispose();
    }
}
