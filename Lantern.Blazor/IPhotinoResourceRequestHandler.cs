namespace Photino.Blazor;

// Resolves resources requested by code (HttpClient) rather than by WebView navigation.
// Unlike IPhotinoWebResourceHandler.HandleWebRequest, it never falls back to the host page.
internal interface IPhotinoResourceRequestHandler
{
    Stream? HandleResourceRequest(string url, out string? contentType);
}
