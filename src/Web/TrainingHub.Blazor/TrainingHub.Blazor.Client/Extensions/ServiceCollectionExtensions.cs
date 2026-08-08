using TrainingHub.Blazor.Client.Infrastructure;
using TrainingHub.GeneratedClients;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace TrainingHub.Blazor.Client.Extensions;

/// <summary>
/// The browser's services. Registered by the WebAssembly entry point, and by nothing else.
/// </summary>
/// <remarks>
/// The host that serves this application deliberately does not call these. It is the other side of
/// what they describe — the origin they call, the identity they read — and adopting them there
/// collided with the BFF's own API client under the shared name <c>Api</c>. Prerendering is off, so
/// there is nothing server-side to render and nothing to register for.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the client needs.
    /// </summary>
    public static IServiceCollection AddDependencies(this IServiceCollection services)
    {
        return services
            .AddServices()
            .AddTrainingHubBlazorClient();
    }

    /// <summary>
    /// Registers the generated HTTP clients behind the BFF.
    /// </summary>
    public static IServiceCollection AddTrainingHubBlazorClient(this IServiceCollection services)
    {
        // Both clients point at the origin that served the application. There is no
        // `Api:BaseAddress` in the browser's configuration any more, and that is the visible half
        // of the change: the front end cannot be pointed at an API at all — it calls its own
        // origin, and the host decides what sits behind /api. See ADR 0009.
        services.AddHttpClient(HttpClientNames.Api, (serviceProvider, client) =>
                client.BaseAddress = new Uri(Origin(serviceProvider), "api/"))
            .AddHttpMessageHandler<RequestedWithHandler>();

        services.AddHttpClient(HttpClientNames.Bff, (serviceProvider, client) =>
                client.BaseAddress = Origin(serviceProvider))
            .AddHttpMessageHandler<RequestedWithHandler>();

        // The generated clients keep talking to "the API" and know nothing of the proxy in front of
        // it: their paths are relative, so /Trainer becomes /api/Trainer with no generated code to
        // adjust. The one thing they cannot do is sign in — see BffSessionClient.
        services.AddHttpClient<ITrainerClient, TrainerClient>(HttpClientNames.Api);
        services.AddHttpClient<ITrainingClient, TrainingClient>(HttpClientNames.Api);
        services.AddHttpClient<IAuthClient, AuthClient>(HttpClientNames.Api);
        // Generated since the administrative endpoints landed and registered only now, because
        // nothing in the browser could reach them until there was a screen to ask from.
        services.AddHttpClient<IAdministrationClient, AdministrationClient>(HttpClientNames.Api);

        return services;
    }

    /// <summary>
    /// Registers the client-side services.
    /// </summary>
    public static IServiceCollection AddServices(this IServiceCollection services)
    {
        services.AddAuthorizationCore();
        services.AddScoped<BffAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<BffAuthenticationStateProvider>());
        services.AddScoped<IBffSessionClient, BffSessionClient>();
        services.AddTransient<RequestedWithHandler>();
        return services;
    }

    /// <summary>The address the application was served from.</summary>
    /// <remarks>
    /// <see cref="IWebAssemblyHostEnvironment"/> rather than <c>NavigationManager</c>, which is
    /// scoped on a server and would throw here: <c>IHttpClientFactory</c> hands these delegates the
    /// root provider. Reaching for a service that exists only in the browser also says plainly
    /// where this registration belongs.
    /// </remarks>
    private static Uri Origin(IServiceProvider serviceProvider) =>
        new(serviceProvider.GetRequiredService<IWebAssemblyHostEnvironment>().BaseAddress);
}
