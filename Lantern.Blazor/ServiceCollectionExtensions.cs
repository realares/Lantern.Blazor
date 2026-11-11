using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using PhotinoX.App;

namespace Photino.Blazor;

/// <summary>
/// Provides service registration helpers for Photino Blazor applications.
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the services required to run a Photino Blazor application.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection.</returns>
    internal static IServiceCollection AddBlazorDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOptions<PhotinoBlazorOptions>()
            .BindConfiguration("PhotinoX:Blazor")
            .Validate(options => options.AppBaseUri is { IsAbsoluteUri: true }, $"{nameof(PhotinoBlazorOptions.AppBaseUri)} must be an absolute URI.")
            .Validate(options => options.AppBaseUri is not { IsAbsoluteUri: true } || IsCustomScheme(options.AppBaseUri),
                $"{nameof(PhotinoBlazorOptions.AppBaseUri)} must use a custom scheme; http, https and file cannot be registered as WebView scheme handlers.")
            .Validate(options => options.AppBaseUri is not { IsAbsoluteUri: true } || IsBaseUri(options.AppBaseUri),
                $"{nameof(PhotinoBlazorOptions.AppBaseUri)} must end with '/' and must not contain a query or fragment.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.HostPage), $"{nameof(PhotinoBlazorOptions.HostPage)} must not be empty.")
            .ValidateOnStart();

        // Window-specific services are regular scoped registrations, so services constructed by the
        // container (not only components) receive the window's HttpClient and resource handler.
        services.TryAddScoped<PhotinoWindowContext>();
        services.TryAddSingleton<PhotinoHttpMessageHandlerProvider>();
        services.TryAddScoped(static serviceProvider =>
            serviceProvider.GetRequiredService<PhotinoWindowContext>().ResourceHandler);
        services.TryAddScoped(static serviceProvider =>
        {
            var context = serviceProvider.GetRequiredService<PhotinoWindowContext>();
            return serviceProvider
                .GetRequiredService<PhotinoHttpMessageHandlerProvider>()
                .CreateClient(context.ResourceHandler, context.AppBaseUri);
        });

        services
            .AddSingleton<IFileProvider>(serviceProvider =>
            {
                var environment = serviceProvider.GetRequiredService<PhotinoEnvironment>();
                return new PhysicalFileProvider(environment.WebRootPath);
            })
            .AddBlazorWebView();

        return services;
    }

    // PhotinoWindow.RegisterCustomSchemeHandler rejects these schemes.
    private static bool IsCustomScheme(Uri uri)
    {
        return uri.Scheme != Uri.UriSchemeHttp
            && uri.Scheme != Uri.UriSchemeHttps
            && uri.Scheme != Uri.UriSchemeFile;
    }

    // Without a trailing slash, Uri.IsBaseOf treats sibling paths (app://localhost/other for
    // app://localhost/app) as part of the application.
    private static bool IsBaseUri(Uri uri)
    {
        return uri.AbsolutePath.EndsWith('/')
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
