using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Photino.NET;

namespace Photino.Blazor;

/// <summary>
/// Represents a Photino window that hosts Blazor content.
/// </summary>
public sealed partial class PhotinoBlazorWindow : IPhotinoWebResourceHandler, IPhotinoResourceRequestHandler
{
    private readonly PhotinoWindowServiceProvider _services;
    private readonly HashSet<string> _externalUriSchemes;
    // Selectors of root components attached to the WebView manager; accessed on the Blazor dispatcher only.
    private readonly HashSet<string> _attachedSelectors = new(StringComparer.Ordinal);
    private bool _isStarted;
    private bool _isTrackingRootComponents;
    private int _disposed;
    private Task? _disposeTask;
    private Uri? _pendingStartUri;

    internal PhotinoBlazorWindow(
        PhotinoWindow window,
        PhotinoWebViewManager webViewManager,
        RootComponentsCollection rootComponents,
        PhotinoWindowServiceProvider services,
        PhotinoBlazorOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(webViewManager);
        ArgumentNullException.ThrowIfNull(rootComponents);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        Window = window;
        WebViewManager = webViewManager;
        RootComponents = rootComponents;
        _services = services;
        _externalUriSchemes = new HashSet<string>(options.ExternalUriSchemes, StringComparer.OrdinalIgnoreCase);

        Window.NavigationStarting += OnNavigationStarting;
        Window.NewWindowRequested += OnNewWindowRequested;
    }

    /// <summary>
    /// Gets the native Photino window.
    /// </summary>
    public PhotinoWindow Window { get; }

    /// <summary>
    /// Gets the root components configured for this window.
    /// </summary>
    /// <remarks>
    /// Components added or removed after the window has started are attached to or removed from the running page.
    /// Adding an invalid component after the window has started throws <see cref="InvalidOperationException"/>;
    /// the component remains in the collection but is not attached.
    /// </remarks>
    public RootComponentsCollection RootComponents { get; }

    internal PhotinoWebViewManager WebViewManager { get; }

    /// <summary>
    /// Occurs before the Blazor WebView loads a top-level URL.
    /// </summary>
    /// <remarks>
    /// Anchor tags with <c>target="_blank"</c> and JavaScript <c>window.open(...)</c>
    /// are opened externally and do not raise this event; requests for application URLs are ignored.
    /// URLs are only opened externally when their scheme is listed in <see cref="PhotinoBlazorOptions.ExternalUriSchemes"/>.
    /// </remarks>
    public event EventHandler<UrlLoadingEventArgs>? UrlLoading;

    private void OnNavigationStarting(object? sender, NavigationStartingEventArgs e)
    {
        // The start navigation is initiated by Start() itself and is not subject to the UrlLoading policy.
        // Match it by URI rather than skipping "the next" event, so an unrelated first navigation
        // (or a platform that raises no event for the start URL) cannot bypass the policy.
        var pendingStartUri = Volatile.Read(ref _pendingStartUri);
        if (pendingStartUri is not null
            && IsSameNavigationTarget(pendingStartUri, e.Uri)
            && Interlocked.CompareExchange(ref _pendingStartUri, null, pendingStartUri) == pendingStartUri)
        {
            return;
        }

        var args = UrlLoadingEventArgs.CreateWithDefaultLoadingStrategy(e.Uri, WebViewManager.AppOriginUri);
        UrlLoading?.Invoke(this, args);

        switch (args.UrlLoadingStrategy)
        {
            case UrlLoadingStrategy.OpenInWebView:
                e.Cancel = false;
                break;

            case UrlLoadingStrategy.OpenExternally:
                e.Cancel = true;
                TryOpenExternally(args.Url);
                break;

            case UrlLoadingStrategy.CancelLoad:
                e.Cancel = true;
                break;

            default:
                e.Cancel = true;
                Window.Log($"Unsupported URL loading strategy: {args.UrlLoadingStrategy}.");
                break;
        }
    }

    private static bool IsSameNavigationTarget(Uri expected, Uri actual)
    {
        return actual.IsAbsoluteUri
            && Uri.Compare(expected, actual, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.Ordinal) == 0;
    }

    private void OnNewWindowRequested(object? sender, NewWindowRequestedEventArgs e)
    {
        // Application URLs use a scheme that only this WebView can resolve, so the operating system cannot open them.
        if (e.Uri.IsAbsoluteUri && WebViewManager.AppOriginUri.IsBaseOf(e.Uri))
        {
            Window.Log($"Ignored new window request for application URL: {e.Uri}.");
            return;
        }

        TryOpenExternally(e.Uri);
    }

    internal static bool IsAllowedExternalUri(Uri uri, IReadOnlySet<string> allowedSchemes)
    {
        return uri.IsAbsoluteUri && allowedSchemes.Contains(uri.Scheme);
    }

    private bool TryOpenExternally(Uri uri)
    {
        // Shell execution runs whatever the OS associates with the URI (executables for file:, protocol handlers
        // for custom schemes), so only explicitly allowed schemes may leave the WebView.
        if (!IsAllowedExternalUri(uri, _externalUriSchemes))
        {
            Window.Log($"Refused to open URL externally because its scheme is not allowed: {uri}.");
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            Window.Log($"Failed to open URL externally: {uri}. {ex}");
            return false;
        }
    }

    /// <summary>
    /// Starts the Blazor content without showing the native window.
    /// </summary>
    internal void Start()
    {
        ThrowIfDisposed();

        if (_isStarted)
            return;

        _isStarted = true;

        var wasInitialized = Window.IsInitialized;

        try
        {
            if (string.IsNullOrWhiteSpace(Window.StartUrl))
                Window.StartUrl = "/";

            if (RootComponents.Count == 0)
                ThrowRootComponentsNotConfigured();

            AttachRootComponentsAsync().GetAwaiter().GetResult();
            TrackRootComponentChanges();

            // WebViewManager.Navigate resolves the start URL against the application base URI.
            Volatile.Write(ref _pendingStartUri, new Uri(WebViewManager.AppOriginUri, Window.StartUrl));
            WebViewManager.Navigate(Window.StartUrl);
        }
        catch
        {
            _isStarted = false;
            throw;
        }

        // PhotinoApplication.Run must be able to create the native window itself (on an STA thread on Windows).
        // A window the caller initialized before Start() is fine; Start() itself must never initialize it.
        Debug.Assert(wasInitialized || !Window.IsInitialized, "Starting Blazor content must not initialize the native window.");
    }

    /// <summary>
    /// Starts the Blazor content and shows the native window on the current thread.
    /// </summary>
    /// <remarks>
    /// On Windows, the calling thread must be an STA thread. Use <see cref="PhotinoBlazorApp.Run"/>
    /// to run the main application window with automatic STA thread handling.
    /// </remarks>
    public void Show()
    {
        Start();
        Window.Show();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Requests for paths without a file extension are treated as navigations and fall back to the host page.
    /// </remarks>
    public Stream? HandleWebRequest(string url, out string? contentType)
    {
        return HandleRequest(url, isResourceRequest: false, out contentType);
    }

    Stream? IPhotinoResourceRequestHandler.HandleResourceRequest(string url, out string? contentType)
    {
        return HandleRequest(url, isResourceRequest: true, out contentType);
    }

    private Stream? HandleRequest(string url, bool isResourceRequest, out string? contentType)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            contentType = null;
            return null;
        }

        return WebViewManager.HandleWebRequestCore(url, isResourceRequest, out contentType);
    }

    internal Stream? HandleWebRequest(object? sender, string scheme, string url, out string? contentType)
    {
        _ = sender;
        _ = scheme;

        return HandleWebRequest(url, out contentType);
    }

    // Starts disposal on the first call and returns the same task to every caller, so callers can wait
    // for a disposal that is already in progress. Must not be called while holding a lock: the synchronous
    // part disposes the renderer, which runs component code and may wait for the dispatcher thread.
    internal Task DisposeAsyncCore()
    {
        var disposeTask = Volatile.Read(ref _disposeTask);
        if (disposeTask is not null)
            return disposeTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        disposeTask = Interlocked.CompareExchange(ref _disposeTask, completion.Task, null);
        if (disposeTask is not null)
            return disposeTask;

        Volatile.Write(ref _disposed, 1);
        _ = DisposeCoreAsync(completion);
        return completion.Task;
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            Window.NavigationStarting -= OnNavigationStarting;
            Window.NewWindowRequested -= OnNewWindowRequested;
            RootComponents.CollectionChanged -= OnRootComponentsChanged;

            try
            {
                await WebViewManager.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _services.DisposeAsync().ConfigureAwait(false);
            }

            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private Task AttachRootComponentsAsync()
    {
        // Validate everything before attaching anything, so an invalid entry cannot leave
        // the window with only some of its components attached.
        var components = RootComponents.ToArray();
        var selectors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            component.Validate();

            if (!selectors.Add(component.Selector))
                ThrowDuplicateSelector(component.Selector);
        }

        return WebViewManager.Dispatcher.InvokeAsync(async () =>
        {
            // Components attached by an earlier, failed Start() are skipped.
            foreach (var component in components)
            {
                if (!_attachedSelectors.Contains(component.Selector))
                    await AttachRootComponentAsync(component);
            }
        });
    }

    // Runs on the Blazor dispatcher, which also serializes access to _attachedSelectors.
    private async Task AttachRootComponentAsync(RootComponent component)
    {
        if (_attachedSelectors.Contains(component.Selector))
            ThrowDuplicateSelector(component.Selector);

        var parameters = component.Parameters is null
            ? ParameterView.Empty
            : ParameterView.FromDictionary(component.Parameters);

        // Record the selector before awaiting: on a running page the await lasts until the component's
        // initial render is quiescent (e.g. OnInitializedAsync), and a removal that arrives in the meantime
        // must find the selector. The WebView manager registers the selector before it renders, so the
        // component stays registered (and removable) even when that render fails.
        _attachedSelectors.Add(component.Selector);
        await WebViewManager.AddRootComponentAsync(component.ComponentType, component.Selector, parameters);
    }

    private void TrackRootComponentChanges()
    {
        if (_isTrackingRootComponents)
            return;

        _isTrackingRootComponents = true;
        RootComponents.CollectionChanged += OnRootComponentsChanged;
    }

    // Applies changes made after Start() to the running page.
    private void OnRootComponentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || e.Action == NotifyCollectionChangedAction.Move)
            return;

        var added = e.NewItems?.Cast<RootComponent>().ToArray() ?? [];

        // Report invalid components to the code that modified the collection.
        foreach (var component in added)
            component.Validate();

        var removed = e.Action == NotifyCollectionChangedAction.Reset
            ? null
            : e.OldItems?.Cast<RootComponent>().Select(component => component.Selector).ToArray() ?? [];

        _ = UpdateRootComponentsAsync(removed, added);
    }

    // removedSelectors == null removes every attached component (collection reset).
    private async Task UpdateRootComponentsAsync(string[]? removedSelectors, RootComponent[] added)
    {
        try
        {
            await WebViewManager.Dispatcher.InvokeAsync(async () =>
            {
                foreach (var selector in removedSelectors ?? [.. _attachedSelectors])
                {
                    if (_attachedSelectors.Remove(selector))
                        await WebViewManager.RemoveRootComponentAsync(selector);
                }

                foreach (var component in added)
                    await AttachRootComponentAsync(component);
            });
        }
        catch (Exception ex)
        {
            Window.Log($"Error updating root components: {ex}");
        }
    }
}