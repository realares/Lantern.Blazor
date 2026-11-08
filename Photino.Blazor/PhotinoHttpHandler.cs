using System.Net;
using System.Net.Http.Headers;

namespace Photino.Blazor;

/// <summary>
/// Handles HTTP requests for resources served by a Photino Blazor host.
/// </summary>
/// <remarks>
/// Requests for application resources are answered by the Photino resource handler. Paths are resolved as
/// static resources and never fall back to the host page. Requests with a non-HTTP scheme (such as the
/// application scheme) that cannot be resolved are answered with <see cref="HttpStatusCode.NotFound"/>.
/// Only HTTP and HTTPS requests that are not handled by Photino are passed to the inner handler.
/// </remarks>
public sealed class PhotinoHttpHandler : DelegatingHandler
{
    private readonly IPhotinoWebResourceHandler _resourceHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotinoHttpHandler"/> class.
    /// </summary>
    /// <param name="resourceHandler">The Photino web resource handler.</param>
    public PhotinoHttpHandler(IPhotinoWebResourceHandler resourceHandler)
        : this(resourceHandler, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotinoHttpHandler"/> class.
    /// </summary>
    /// <param name="resourceHandler">The Photino web resource handler.</param>
    /// <param name="innerHandler">The inner HTTP handler used for requests not handled by Photino.</param>
    public PhotinoHttpHandler(IPhotinoWebResourceHandler resourceHandler, HttpMessageHandler? innerHandler)
    {
        ArgumentNullException.ThrowIfNull(resourceHandler);

        _resourceHandler = resourceHandler;

        // The last inner handler in the pipeline must be a real HTTP handler.
        // Use HttpClientHandler for requests that are not handled by Photino.
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri;

        if (requestUri is null || !requestUri.IsAbsoluteUri)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var isHttpRequest = requestUri.Scheme == Uri.UriSchemeHttp || requestUri.Scheme == Uri.UriSchemeHttps;

        // Application resources are static files; there is no server that could accept other methods.
        if (!isHttpRequest && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return CreateResponse(request, HttpStatusCode.MethodNotAllowed);

        var content = HandleResourceRequest(requestUri.AbsoluteUri, out var contentType);

        if (content is not null)
        {
            var response = CreateResponse(request, HttpStatusCode.OK);
            response.Content = new StreamContent(content);

            if (!string.IsNullOrWhiteSpace(contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                response.Content.Headers.ContentType = mediaType;
            }

            return response;
        }

        // The network handler cannot serve custom schemes, so an unresolved application resource is a 404.
        if (!isHttpRequest)
            return CreateResponse(request, HttpStatusCode.NotFound);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Stream? HandleResourceRequest(string url, out string? contentType)
    {
        return _resourceHandler is IPhotinoResourceRequestHandler resourceRequestHandler
            ? resourceRequestHandler.HandleResourceRequest(url, out contentType)
            : _resourceHandler.HandleWebRequest(url, out contentType);
    }

    private static HttpResponseMessage CreateResponse(HttpRequestMessage request, HttpStatusCode statusCode)
    {
        return new HttpResponseMessage(statusCode)
        {
            RequestMessage = request
        };
    }
}
