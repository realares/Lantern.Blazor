using Microsoft.Extensions.Options;

namespace Photino.Blazor;

// Scoped holder for the window that owns a service scope. Window scopes are created through
// PhotinoWindowServiceProvider, which binds the window's resource handler; scopes created elsewhere
// keep a handler that serves nothing.
internal sealed class PhotinoWindowContext
{
    private static readonly IPhotinoWebResourceHandler s_noResources = new PhotinoWindowResourceHandler();

    public PhotinoWindowContext(IOptions<PhotinoBlazorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AppBaseUri = options.Value.AppBaseUri;
    }

    public Uri AppBaseUri { get; }

    public IPhotinoWebResourceHandler ResourceHandler { get; private set; } = s_noResources;

    internal void Bind(IPhotinoWebResourceHandler resourceHandler)
    {
        ArgumentNullException.ThrowIfNull(resourceHandler);
        ResourceHandler = resourceHandler;
    }
}
