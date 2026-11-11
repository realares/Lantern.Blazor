using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Photino.NET;
using PhotinoX.App;

namespace Photino.Blazor;

/// <summary>
/// Represents a Photino Blazor application.
/// </summary>
public sealed partial class PhotinoBlazorApp : IDisposable, IAsyncDisposable
{
    private static class States
    {
        public const int NotDisposed = 0;// default value of _state
        public const int Disposing = 1;
        public const int Disposed = 2;
    }

    // Windows that have not finished disposing.
    private readonly List<PhotinoBlazorWindow> _blazorWindows = [];
    private PhotinoBlazorWindow? _mainBlazorWindow;
    private Action<PhotinoBlazorApp>? _beforeDispose;
    private int _beforeDisposeStarted;
    private int _isRunning;
    private int _state;
    private int _windowsDisposed;
    private readonly PhotinoApp _app;

    internal PhotinoBlazorApp(PhotinoApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    /// <summary>
    /// Gets the application's service provider.
    /// </summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>
    /// Gets the application's configured configuration.
    /// </summary>
    public IConfiguration Configuration => _app.Configuration;

    /// <summary>
    /// Gets information about the application's environment.
    /// </summary>
    public PhotinoEnvironment Environment => _app.Environment;

    /// <summary>
    /// Gets the underlying Photino application.
    /// </summary>
    public PhotinoApplication Application => _app.Application;

    /// <summary>
    /// Gets the dispatcher associated with the underlying Photino application.
    /// </summary>
    public PhotinoDispatcher Dispatcher => _app.Dispatcher;

    /// <summary>
    /// Gets the main Photino Blazor window.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the main Blazor window has not been initialized yet.
    /// </exception>
    public PhotinoBlazorWindow MainBlazorWindow
    {
        get => _mainBlazorWindow ?? ThrowMainWindowNotInitialized();
        private set => _mainBlazorWindow = value;
    }

    /// <summary>
    /// Gets the native Photino window for the main Blazor window.
    /// </summary>
    public PhotinoWindow MainWindow => MainBlazorWindow.Window;

    internal void Initialize(RootComponentsCollection rootComponents, Action<PhotinoWindow>? configureMainWindow)
    {
        ArgumentNullException.ThrowIfNull(rootComponents);

        // Share the builder's JS component store: registrations made through
        // builder.RootComponents.RegisterForJavaScript(...) live only there.
        MainBlazorWindow = CreateWindowCore(
            parent: null,
            configurationName: null,
            configure: configureMainWindow,
            jsComponents: rootComponents.JSComponents);

        foreach (var component in rootComponents)
        {
            MainBlazorWindow.RootComponents.Add(new RootComponent
            {
                ComponentType = component.ComponentType,
                Selector = component.Selector,
                Parameters = component.Parameters is null
                    ? null
                    : new Dictionary<string, object?>(component.Parameters)
            });
        }
    }

    /// <summary>
    /// Creates a new Photino Blazor window.
    /// </summary>
    /// <param name="configurationName">The optional name of the window configuration to apply. If omitted, only the default window settings are applied.</param>
    /// <param name="configure">An optional delegate used to configure the window before its WebView is initialized.</param>
    /// <returns>A configured Photino Blazor window.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the application has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the main window has not been initialized.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="configurationName"/> is empty or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the specified named window configuration is not found.</exception>
    public PhotinoBlazorWindow CreateWindow(string? configurationName = null, Action<PhotinoWindow>? configure = null)
    {
        ThrowIfDisposingOrDisposed();
        ThrowIfWindowsDisposed();

        if (configurationName is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationName);

        return CreateWindowCore(MainWindow, configurationName, configure);
    }

    private PhotinoBlazorWindow CreateWindowCore(
        PhotinoWindow? parent,
        string? configurationName,
        Action<PhotinoWindow>? configure = null,
        JSComponentConfigurationStore? jsComponents = null)
    {
        var window = parent is null ? new PhotinoWindow() : new PhotinoWindow(parent);

        var settings = Services.GetService<IOptions<PhotinoAppSettings>>()?.Value;

        // Settings are applied without PhotinoEnvironment on purpose: the environment is only used to
        // resolve StartUrl to a file path under WebRootPath, which would bypass the app:// scheme that
        // serves the Blazor host page. Start() resolves StartUrl against AppBaseUri instead.
        if (parent is null)
        {
            window
                .SetTitle("Lantern.Blazor App")
                .SetWidth(1000)
                .SetHeight(900)
                .Center();

            if (settings is not null)
                window.ApplyMainWindowSettings(settings);
        }
        else if (settings is not null)
        {
            if (configurationName is null)
                window.ApplySettings(settings.WindowDefaults);
            else
                window.ApplyWindowSettings(settings, configurationName);
        }

        configure?.Invoke(window);

        var rootComponents = jsComponents is null
            ? new RootComponentsCollection()
            : new RootComponentsCollection(jsComponents);

        var synchronizationContext = new PhotinoSynchronizationContext(new PhotinoUiDispatcher(Dispatcher, Application));
        var dispatcher = new PhotinoBlazorDispatcher(synchronizationContext);

        var resourceHandler = new PhotinoWindowResourceHandler();
        var options = Services.GetRequiredService<IOptions<PhotinoBlazorOptions>>();

        var windowServices = new PhotinoWindowServiceProvider(Services, resourceHandler);
        PhotinoWebViewManager? webViewManager = null;
        PhotinoBlazorWindow? blazorWindow = null;

        try
        {
            webViewManager = new PhotinoWebViewManager(
                window,
                windowServices,
                dispatcher,
                windowServices.GetRequiredService<IFileProvider>(),
                rootComponents.JSComponents,
                options);

            blazorWindow = new PhotinoBlazorWindow(window, webViewManager, rootComponents, windowServices, options.Value);

            resourceHandler.Handler = blazorWindow;

            window.RegisterCustomSchemeHandler(options.Value.AppBaseUri.Scheme, blazorWindow.HandleWebRequest);
        }
        catch
        {
            // The window is not tracked yet, so release its service scope and message pumps here.
            CleanUpFailedWindow(window, blazorWindow, webViewManager, windowServices);
            throw;
        }

        bool disposeWindow;

        lock (_blazorWindows)
        {
            disposeWindow = Volatile.Read(ref _windowsDisposed) != 0;

            if (!disposeWindow)
                _blazorWindows.Add(blazorWindow);
        }

        if (disposeWindow)
        {
            blazorWindow.DisposeAsyncCore().GetAwaiter().GetResult();
            ThrowApplicationDisposed();
        }

        window.RegisterClosedHandler((_, _) =>
        {
            // Disposal runs component code and may wait for the dispatcher thread, so it is started outside
            // any lock. The window stays tracked until disposal completes, so DisposeWindowsAsync can await it.
            _ = blazorWindow.DisposeAsyncCore().ContinueWith(
                static (task, state) =>
                {
                    var (app, closedWindow) = ((PhotinoBlazorApp, PhotinoBlazorWindow))state!;
                    app.OnWindowDisposed(closedWindow, task);
                },
                (this, blazorWindow),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });

        return blazorWindow;
    }

    private static void CleanUpFailedWindow(
        PhotinoWindow window,
        PhotinoBlazorWindow? blazorWindow,
        PhotinoWebViewManager? webViewManager,
        PhotinoWindowServiceProvider windowServices)
    {
        try
        {
            if (blazorWindow is not null)
            {
                blazorWindow.DisposeAsyncCore().GetAwaiter().GetResult();
                return;
            }

            try
            {
                webViewManager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                windowServices.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            window.Log($"Error cleaning up after failed Photino Blazor window creation: {ex}");
        }
    }

    private void OnWindowDisposed(PhotinoBlazorWindow window, Task disposeTask)
    {
        lock (_blazorWindows)
            _blazorWindows.Remove(window);

        if (disposeTask.IsFaulted)
            window.Window.Log($"Error disposing Photino Blazor window: {disposeTask.Exception}");
    }

    /// <summary>
    /// Creates a new Photino Blazor window with a root component.
    /// </summary>
    /// <typeparam name="TComponent">The Blazor component type.</typeparam>
    /// <param name="selector">The CSS selector that identifies where the component is rendered in the host page.</param>
    /// <param name="parameters">Optional component parameters.</param>
    /// <param name="configurationName">The optional name of the window configuration to apply. If omitted, only the default window settings are applied.</param>
    /// <param name="configure">An optional delegate used to configure the window before its WebView is initialized.</param>
    /// <returns>A configured Photino Blazor window.</returns>
    public PhotinoBlazorWindow CreateWindow<TComponent>(string selector, IDictionary<string, object?>? parameters = null, string? configurationName = null, Action<PhotinoWindow>? configure = null) where TComponent : IComponent
    {
        return CreateWindow(typeof(TComponent), selector, parameters, configurationName, configure);
    }

    /// <summary>
    /// Creates a new Photino Blazor window with a root component.
    /// </summary>
    /// <param name="componentType">The Blazor component type.</param>
    /// <param name="selector">The CSS selector that identifies where the component is rendered in the host page.</param>
    /// <param name="parameters">Optional component parameters.</param>
    /// <param name="configurationName">The optional name of the window configuration to apply. If omitted, only the default window settings are applied.</param>
    /// <param name="configure">An optional delegate used to configure the window before its WebView is initialized.</param>
    /// <returns>A configured Photino Blazor window.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="componentType"/> is <see langword="null"/>.</exception>
    public PhotinoBlazorWindow CreateWindow(Type componentType, string selector, IDictionary<string, object?>? parameters = null, string? configurationName = null, Action<PhotinoWindow>? configure = null)
    {
        // Validate before creating the window: a window created here is tracked until the application
        // is disposed, and the caller would never receive it if the component turned out to be invalid.
        ArgumentNullException.ThrowIfNull(componentType);

        var component = new RootComponent
        {
            ComponentType = componentType,
            Selector = selector,
            Parameters = parameters is null ? null : new Dictionary<string, object?>(parameters)
        };

        component.Validate();

        var window = CreateWindow(configurationName, configure);
        window.RootComponents.Add(component);
        return window;
    }

    /// <summary>
    /// Starts the main Blazor content, shows the main window, and runs the Photino application message loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows, the underlying Photino application runs native window initialization and the message loop
    /// on an STA thread when the calling thread is not an STA thread.
    /// </para>
    /// <para>
    /// This method does not dispose the application after the message loop exits.
    /// The caller is responsible for disposing the application.
    /// </para>
    /// </remarks>
    /// <returns>The application exit code.</returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the application has already been disposed.
    /// </exception>
    public int Run()
    {
        ThrowIfDisposingOrDisposed();

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            ThrowApplicationAlreadyRunning();

        try
        {
            var mainBlazorWindow = MainBlazorWindow;
            mainBlazorWindow.Start();
            return _app.Run(mainBlazorWindow.Window);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    /// <summary>
    /// Releases the resources used by the application, its Blazor windows, and application services.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, States.Disposing, States.NotDisposed) != States.NotDisposed)
            return;
        try
        {
            _app.Dispose();
        }
        finally
        {
            Volatile.Write(ref _state, States.Disposed);
        }
    }

    /// <summary>
    /// Asynchronously releases the resources used by the application.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _state, States.Disposing, States.NotDisposed) != States.NotDisposed)
            return;

        try
        {
            // Run the callbacks and await the window disposal here; the application's before-dispose hook
            // is synchronous and would otherwise block this call until every renderer is disposed.
            ExceptionDispatchInfo? beforeDisposeException = null;

            try
            {
                await RunBeforeDisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                beforeDisposeException = ExceptionDispatchInfo.Capture(ex);
            }

            await _app.DisposeAsync().ConfigureAwait(false);
            beforeDisposeException?.Throw();
        }
        finally
        {
            Volatile.Write(ref _state, States.Disposed);
        }
    }

    internal void SetBeforeDispose(Action<PhotinoBlazorApp>? callbacks)
    {
        _beforeDispose = callbacks;
    }

    // Invoked by the application's before-dispose hook. Runs the callbacks and disposes the windows
    // unless DisposeAsync already did.
    internal void RunBeforeDispose()
    {
        if (!TryBeginBeforeDispose(out var callbacks))
            return;

        try
        {
            callbacks?.Invoke(this);
        }
        finally
        {
            try
            {
                DisposeWindowsAsync().GetAwaiter().GetResult();
            }
            finally
            {
                MarkDisposed();
            }
        }
    }

    private async Task RunBeforeDisposeAsync()
    {
        if (!TryBeginBeforeDispose(out var callbacks))
            return;

        try
        {
            callbacks?.Invoke(this);
        }
        finally
        {
            try
            {
                await DisposeWindowsAsync().ConfigureAwait(false);
            }
            finally
            {
                MarkDisposed();
            }
        }
    }

    private bool TryBeginBeforeDispose(out Action<PhotinoBlazorApp>? callbacks)
    {
        if (Interlocked.Exchange(ref _beforeDisposeStarted, 1) != 0)
        {
            callbacks = null;
            return false;
        }

        callbacks = Interlocked.Exchange(ref _beforeDispose, null);
        return true;
    }

    private async Task DisposeWindowsAsync()
    {
        if (Interlocked.Exchange(ref _windowsDisposed, 1) != 0)
            return;

        PhotinoBlazorWindow[] windows;

        lock (_blazorWindows)
        {
            windows = [.. _blazorWindows];
            _blazorWindows.Clear();
        }

        if (windows.Length == 0)
            return;

        // Started outside the lock; windows that are already being disposed return their in-flight task.
        var disposeTasks = new Task[windows.Length];
        for (var i = 0; i < windows.Length; i++)
            disposeTasks[i] = windows[i].DisposeAsyncCore();

        var allDisposed = Task.WhenAll(disposeTasks);

        try
        {
            await allDisposed.ConfigureAwait(false);
        }
        catch
        {
            _mainBlazorWindow?.Window.Log($"Error disposing Photino Blazor windows: {allDisposed.Exception}");
        }
    }

    internal void MarkDisposed()
    {
        Volatile.Write(ref _state, States.Disposed);
    }
}