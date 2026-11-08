using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PhotinoBlazorWindowTests
{
    private const string HostPageContent = "<html><body><app></app></body></html>";
    private const string DataContent = "{\"value\":42}";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    // PhotinoApp allows only one application per process, so this class builds the only one and shares it.
    private static PhotinoBlazorAppBuilder s_builder = null!;
    private static PhotinoBlazorApp s_app = null!;
    private static string s_webRoot = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        s_webRoot = Directory.CreateTempSubdirectory("photinox-blazor-tests-").FullName;
        File.WriteAllText(Path.Combine(s_webRoot, "index.html"), HostPageContent);
        File.WriteAllText(Path.Combine(s_webRoot, "data.json"), DataContent);

        s_builder = PhotinoBlazorApp.CreateBuilder(useDefaults: false);
        s_builder.UseFileProvider(_ => new PhysicalFileProvider(s_webRoot));
        _ = context;
        s_builder.RootComponents.Add<TestComponent>("app");
        s_builder.RootComponents.RegisterForJavaScript<TestComponent>("test-component");
        s_builder.ConfigureBeforeDispose(app =>
        {
            Interlocked.Increment(ref s_beforeDisposeCount);
            using var content = app.MainBlazorWindow.HandleWebRequest("app://localhost/data.json", out _);
            s_beforeDisposeSawLiveWindow = content is not null;
        });

        s_app = s_builder.Build();
    }

    private static int s_beforeDisposeCount;
    private static bool s_beforeDisposeSawLiveWindow;

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        var mainWindow = s_app.MainBlazorWindow;

        await s_app.DisposeAsync().AsTask().WaitAsync(s_timeout);
        Directory.Delete(s_webRoot, recursive: true);

        // DisposeAsync runs the callbacks once, while windows are still alive, and then disposes the windows.
        Assert.AreEqual(1, s_beforeDisposeCount);
        Assert.IsTrue(s_beforeDisposeSawLiveWindow);
        Assert.IsNull(mainWindow.HandleWebRequest("app://localhost/data.json", out _));
    }

    [TestMethod]
    public async Task DisposeAsyncCore_ReturnsSameTaskToConcurrentCallers()
    {
        var window = s_app.CreateWindow<TestComponent>("app");

        var first = window.DisposeAsyncCore();
        var second = window.DisposeAsyncCore();

        Assert.AreSame(first, second);
        await first.WaitAsync(s_timeout);
        Assert.IsNull(window.HandleWebRequest("app://localhost/data.json", out _));
    }

    [TestMethod]
    public void Start_DoesNotInitializeNativeWindow()
    {
        s_app.MainBlazorWindow.Start();

        // PhotinoApplication.Run must be able to create the native window itself (on an STA thread on Windows).
        Assert.IsFalse(s_app.MainWindow.IsInitialized);
    }

    [TestMethod]
    public void Build_MainWindowUsesBuilderJSComponents()
    {
        Assert.AreSame(s_builder.RootComponents.JSComponents, s_app.MainBlazorWindow.RootComponents.JSComponents);
    }

    [TestMethod]
    public void CreateWindow_UsesOwnJSComponents()
    {
        var window = s_app.CreateWindow<TestComponent>("app");

        Assert.AreNotSame(s_app.MainBlazorWindow.RootComponents.JSComponents, window.RootComponents.JSComponents);
    }

    [TestMethod]
    public void NavigationStarting_OnlySkipsUrlLoadingForStartUri()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        var raised = new List<Uri>();
        window.UrlLoading += (_, e) =>
        {
            raised.Add(e.Url);
            e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
        };

        window.Start();

        // An unrelated navigation before the start navigation must still go through the policy.
        Assert.IsTrue(RaiseNavigationStarting(window, "https://example.com/"));
        Assert.IsFalse(RaiseNavigationStarting(window, "app://localhost/"));
        Assert.IsTrue(RaiseNavigationStarting(window, "app://localhost/"));

        CollectionAssert.AreEqual(
            new[] { new Uri("https://example.com/"), new Uri("app://localhost/") },
            raised);
    }

    // PhotinoWindow raises NavigationStarting from an internal method that the native layer calls.
    // Returns whether the navigation was canceled.
    private static bool RaiseNavigationStarting(PhotinoBlazorWindow window, string uri)
    {
        var method = typeof(Photino.NET.PhotinoWindow).GetMethod(
            "OnNavigationStarting",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(string)]);

        Assert.IsNotNull(method, "PhotinoWindow.OnNavigationStarting(string) was not found.");
        return Convert.ToInt32(method.Invoke(window.Window, [uri])) != 0;
    }

    [TestMethod]
    public async Task SendMessage_AfterWindowDisposed_ThrowsJSDisconnectedException()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        await window.DisposeAsyncCore().WaitAsync(s_timeout);

        // SendMessage is the protected transport hook that Blazor's JS interop and renderer call.
        var sendMessage = typeof(PhotinoWebViewManager).GetMethod(
            "SendMessage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(string)]);
        Assert.IsNotNull(sendMessage);

        var exception = Assert.ThrowsExactly<System.Reflection.TargetInvocationException>(
            () => sendMessage.Invoke(window.WebViewManager, ["__bwv:[]"]));

        Assert.IsInstanceOfType<Microsoft.JSInterop.JSDisconnectedException>(exception.InnerException);
    }

    [TestMethod]
    public void Start_InvalidComponentDoesNotPreventRetry()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        var invalid = new RootComponent { Selector = "#invalid" };
        window.RootComponents.Add(invalid);

        Assert.ThrowsExactly<InvalidOperationException>(window.Start);

        window.RootComponents.Remove(invalid);
        window.Start();

        Assert.IsTrue(IsAttached(window, "app"));
    }

    [TestMethod]
    public void Start_ThrowsForDuplicateSelectors()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        window.RootComponents.Add<TestComponent>("app");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(window.Start);

        StringAssert.Contains(exception.Message, "'app'");
    }

    [TestMethod]
    public void RootComponents_ChangesAfterStartAreAppliedToWindow()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        window.Start();

        window.RootComponents.Add<TestComponent>("#footer");
        Assert.IsTrue(IsAttached(window, "#footer"));

        window.RootComponents.RemoveAt(window.RootComponents.Count - 1);
        Assert.IsFalse(IsAttached(window, "#footer"));

        window.RootComponents.Clear();
        Assert.IsFalse(IsAttached(window, "app"));
    }

    [TestMethod]
    public void RootComponents_InvalidComponentAddedAfterStartThrows()
    {
        var window = s_app.CreateWindow<TestComponent>("app");
        window.Start();

        Assert.ThrowsExactly<InvalidOperationException>(() => window.RootComponents.Add(new RootComponent { Selector = "#x" }));
    }

    // WebViewManager rejects a second root component with the same selector, which reveals whether
    // the selector is currently attached. The probe component is removed again.
    private static bool IsAttached(PhotinoBlazorWindow window, string selector)
    {
        var manager = window.WebViewManager;

        try
        {
            manager.Dispatcher.InvokeAsync(() =>
                    manager.AddRootComponentAsync(typeof(TestComponent), selector, ParameterView.Empty))
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        manager.Dispatcher.InvokeAsync(() => manager.RemoveRootComponentAsync(selector)).GetAwaiter().GetResult();
        return false;
    }

    [TestMethod]
    public void HandleWebRequest_ExtensionlessPathFallsBackToHostPage()
    {
        using var content = s_app.MainBlazorWindow.HandleWebRequest("app://localhost/settings", out var contentType);

        Assert.IsNotNull(content);
        Assert.AreEqual(HostPageContent, new StreamReader(content).ReadToEnd());
        StringAssert.StartsWith(contentType, "text/html");
    }

    [TestMethod]
    public async Task HttpClient_ReturnsStaticResource()
    {
        using var http = CreateHttpClient();

        using var response = await http.GetAsync("data.json").WaitAsync(s_timeout);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(DataContent, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("api/items")]
    [DataRow("missing.json")]
    public async Task HttpClient_ReturnsNotFoundForMissingResource(string path)
    {
        using var http = CreateHttpClient();

        using var response = await http.GetAsync(path).WaitAsync(s_timeout);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task HttpClient_RejectsNonGetMethodsForApplicationResources()
    {
        using var http = CreateHttpClient();

        using var response = await http.PostAsync("data.json", new StringContent("{}")).WaitAsync(s_timeout);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new PhotinoHttpHandler(s_app.MainBlazorWindow))
        {
            BaseAddress = new Uri("app://localhost/")
        };
    }

    private sealed class TestComponent : ComponentBase;
}
