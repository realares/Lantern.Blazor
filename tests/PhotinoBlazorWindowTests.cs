using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PhotinoBlazorWindowTests
{
    // PhotinoApp allows only one application per process, so this class builds the only one and shares it.
    private static PhotinoBlazorAppBuilder s_builder = null!;
    private static PhotinoBlazorApp s_app = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_builder = PhotinoBlazorApp.CreateBuilder(useDefaults: false);
        s_builder.RootComponents.Add<TestComponent>("app");
        s_builder.RootComponents.RegisterForJavaScript<TestComponent>("test-component");

        s_app = s_builder.Build();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_app.Dispose();
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

    private sealed class TestComponent : ComponentBase;
}
