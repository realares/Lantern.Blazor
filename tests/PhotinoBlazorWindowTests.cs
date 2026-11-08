using Microsoft.AspNetCore.Components;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PhotinoBlazorWindowTests
{
    // PhotinoApp allows only one application per process, so keep this the only test that builds one.
    [TestMethod]
    public void Start_DoesNotInitializeNativeWindow()
    {
        var builder = PhotinoBlazorApp.CreateBuilder(useDefaults: false);
        builder.RootComponents.Add<TestComponent>("app");

        using var app = builder.Build();

        app.MainBlazorWindow.Start();

        // PhotinoApplication.Run must be able to create the native window itself (on an STA thread on Windows).
        Assert.IsFalse(app.MainWindow.IsInitialized);
    }

    private sealed class TestComponent : ComponentBase;
}
