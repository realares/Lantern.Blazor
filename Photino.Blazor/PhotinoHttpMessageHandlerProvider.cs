namespace Photino.Blazor;

// Owns the network handler shared by all window HttpClient instances, so external requests reuse one
// connection pool instead of creating a new pool for every service scope.
internal sealed class PhotinoHttpMessageHandlerProvider : IDisposable
{
    private readonly SocketsHttpHandler _sharedHandler = new()
    {
        // Recycle pooled connections so long-running desktop apps pick up DNS changes.
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    };

    public HttpClient CreateClient(IPhotinoWebResourceHandler resourceHandler, Uri baseAddress)
    {
        // disposeHandler: false keeps the shared handler alive when a scoped HttpClient is disposed;
        // the PhotinoHttpHandler wrapper itself holds no other resources.
        return new HttpClient(new PhotinoHttpHandler(resourceHandler, _sharedHandler), disposeHandler: false)
        {
            BaseAddress = baseAddress
        };
    }

    public void Dispose()
    {
        _sharedHandler.Dispose();
    }
}
