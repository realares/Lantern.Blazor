using System.Net;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
public sealed class PhotinoHttpHandlerTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task SendAsync_UnresolvedCustomSchemeRequest_ReturnsNotFound()
    {
        using var http = new HttpClient(new PhotinoHttpHandler(new NullResourceHandler()));

        using var response = await http.GetAsync("app://localhost/missing.json").WaitAsync(s_timeout);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task SendAsync_UnresolvedHttpRequest_IsPassedToInnerHandler()
    {
        var inner = new RecordingHandler();
        using var http = new HttpClient(new PhotinoHttpHandler(new NullResourceHandler(), inner));

        using var response = await http.GetAsync("https://example.com/data").WaitAsync(s_timeout);

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual(new Uri("https://example.com/data"), inner.LastRequestUri);
    }

    private sealed class NullResourceHandler : IPhotinoWebResourceHandler
    {
        public Stream? HandleWebRequest(string url, out string? contentType)
        {
            contentType = null;
            return null;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request });
        }
    }
}
