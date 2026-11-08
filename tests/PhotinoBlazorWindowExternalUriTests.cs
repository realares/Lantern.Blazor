using Microsoft.Extensions.Configuration;
using Photino.Blazor;

namespace PhotinoX.Blazor.Tests;

[TestClass]
public sealed class PhotinoBlazorWindowExternalUriTests
{
    private static readonly IReadOnlySet<string> s_defaultSchemes =
        new HashSet<string>(new PhotinoBlazorOptions().ExternalUriSchemes, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    [DataRow("https://example.com/")]
    [DataRow("http://example.com/path?query=1")]
    [DataRow("mailto:someone@example.com")]
    [DataRow("HTTPS://EXAMPLE.COM/")]
    public void IsAllowedExternalUri_AllowsDefaultSchemes(string url)
    {
        Assert.IsTrue(PhotinoBlazorWindow.IsAllowedExternalUri(new Uri(url), s_defaultSchemes));
    }

    [TestMethod]
    [DataRow("file:///C:/Windows/System32/calc.exe")]
    [DataRow("ms-msdt:/id PCWDiagnostic")]
    [DataRow("search-ms:query=test")]
    [DataRow("app://localhost/settings")]
    [DataRow("javascript:alert(1)")]
    public void IsAllowedExternalUri_RejectsOtherSchemes(string url)
    {
        Assert.IsFalse(PhotinoBlazorWindow.IsAllowedExternalUri(new Uri(url), s_defaultSchemes));
    }

    [TestMethod]
    public void IsAllowedExternalUri_RejectsRelativeUri()
    {
        Assert.IsFalse(PhotinoBlazorWindow.IsAllowedExternalUri(new Uri("settings", UriKind.Relative), s_defaultSchemes));
    }

    [TestMethod]
    public void ExternalUriSchemes_BindsAdditionalSchemesFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PhotinoX:Blazor:ExternalUriSchemes:0"] = "tel"
            })
            .Build();

        var options = new PhotinoBlazorOptions();
        configuration.GetSection("PhotinoX:Blazor").Bind(options);

        Assert.IsTrue(options.ExternalUriSchemes.Contains("tel"));
        Assert.IsTrue(options.ExternalUriSchemes.Contains(Uri.UriSchemeHttps));
    }

    [TestMethod]
    public void ExternalUriSchemes_CanBeExtended()
    {
        var options = new PhotinoBlazorOptions();
        options.ExternalUriSchemes.Add("TEL");

        var schemes = new HashSet<string>(options.ExternalUriSchemes, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(PhotinoBlazorWindow.IsAllowedExternalUri(new Uri("tel:+491234"), schemes));
    }
}
