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
    public static void ClassInitialize(TestContext _)
    {
        s_webRoot = Directory.CreateTempSubdirectory("photinox-blazor-tests-").FullName;
        File.WriteAllText(Path.Combine(s_webRoot, "index.html"), HostPageContent);
        File.WriteAllText(Path.Combine(s_webRoot, "data.json"), DataContent);

        s_builder = PhotinoBlazorApp.CreateBuilder(useDefaults: false);
        s_builder.UseFileProvider(_ => new PhysicalFileProvider(s_webRoot));
        s_builder.RootComponents.Add<TestComponent>("app");
        s_builder.RootComponents.RegisterForJavaScript<TestComponent>("test-component");

        s_app = s_builder.Build();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_app.Dispose();
        Directory.Delete(s_webRoot, recursive: true);
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
