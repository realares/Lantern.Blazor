namespace Photino.Blazor;

internal sealed class PhotinoWindowResourceHandler : IPhotinoWebResourceHandler, IPhotinoResourceRequestHandler
{
    public IPhotinoWebResourceHandler? Handler { get; set; }

    public Stream? HandleWebRequest(string url, out string? contentType)
    {
        if (Handler is null)
        {
            contentType = null;
            return null;
        }

        return Handler.HandleWebRequest(url, out contentType);
    }

    public Stream? HandleResourceRequest(string url, out string? contentType)
    {
        switch (Handler)
        {
            case IPhotinoResourceRequestHandler resourceHandler:
                return resourceHandler.HandleResourceRequest(url, out contentType);

            case not null:
                return Handler.HandleWebRequest(url, out contentType);

            default:
                contentType = null;
                return null;
        }
    }
}
