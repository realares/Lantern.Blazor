namespace Photino.Blazor;

/// <summary>
/// Configures the Photino Blazor host.
/// </summary>
public sealed class PhotinoBlazorOptions
{
    /// <summary>
    /// Gets or sets the base URI used by the Blazor WebView host.
    /// </summary>
    public Uri AppBaseUri { get; set; } = PhotinoWebViewManager.AppBaseUri;

    /// <summary>
    /// Gets or sets the host page served as the Blazor application entry point.
    /// </summary>
    public string HostPage { get; set; } = "index.html";

    /// <summary>
    /// Gets the URI schemes that may be handed to the operating system when a URL is opened externally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to <see cref="UrlLoadingStrategy.OpenExternally"/> and to <c>target="_blank"</c> links and
    /// <c>window.open(...)</c> requests. URLs with any other scheme, such as <c>file:</c> or custom protocol
    /// handlers, are not opened, because the operating system would execute them on behalf of web content.
    /// </para>
    /// <para>
    /// The defaults are <c>http</c>, <c>https</c>, and <c>mailto</c>. Scheme names are compared case-insensitively.
    /// </para>
    /// </remarks>
    public ISet<string> ExternalUriSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeMailto
    };
}
