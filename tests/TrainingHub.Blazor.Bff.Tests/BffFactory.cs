using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Forwarder;

namespace TrainingHub.Blazor.Bff.Tests;

/// <summary>
/// Hosts the real BFF — the actual <c>Program.cs</c>, its pipeline order included — with the API
/// replaced by a handler that records what it was sent.
/// </summary>
/// <remarks>
/// Nothing about the security behavior is stubbed: cookie authentication, the forgery guard, the
/// authorization on the proxied route and the token transform are the production ones. Only the far
/// side of the proxy is fake, because a test cannot assert on a credential it never sees.
/// <para>
/// The environment is <c>Development</c>, so the host's own <c>appsettings.Development.json</c>
/// supplies <c>Api:BaseAddress</c>. Its value is never dialled: both the login client and the proxy
/// are pointed at the handlers below. A developer's <c>appsettings.Local.json</c> is removed from
/// the configuration for the same hermetic reason the API factory removes it (ADR 0035).
/// </para>
/// </remarks>
/// <param name="turnstileSiteKey">The site key the host runs with — <see cref="TurnstileSiteKey"/>
/// unless a fact says otherwise. <see langword="null"/> leaves the key unconfigured.</param>
/// <param name="turnstileSecretKey">The secret's counterpart, with the same default and the same
/// null meaning, so a fact can configure the whole pair, none of it, or — to prove the startup
/// refusal — half of it.</param>
public sealed class BffFactory(
    string? turnstileSiteKey = BffFactory.TurnstileSiteKey,
    string? turnstileSecretKey = BffFactory.TurnstileSecretKey) : WebApplicationFactory<Program>
{
    /// <summary>The API as the BFF's own sign-in endpoint reaches it.</summary>
    public RecordingHandler LoginApi { get; } = new();

    /// <summary>The API as the proxy reaches it.</summary>
    public RecordingHandler ProxiedApi { get; } = new();

    /// <summary>Cloudflare's siteverify endpoint, as the contact endpoint reaches it.</summary>
    /// <remarks>
    /// Its default answer is an empty document, which parses to a refusal: a fact that wants a
    /// token admitted says so explicitly, and a fact that forgets cannot pass by accident.
    /// </remarks>
    public RecordingHandler TurnstileApi { get; } = new();

    /// <summary>The site key the suite configures, for facts that assert it is served.</summary>
    public const string TurnstileSiteKey = "test-site-key";

    /// <summary>The secret the suite configures, for facts that assert where it travels.</summary>
    public const string TurnstileSecretKey = "test-secret-key";

    /// <summary>
    /// Configure web host.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // The Development file names the local dashboard's OTLP endpoint, and this factory runs
        // under Development; blanked so the suite never opens an exporter toward a collector that
        // is not there — the same neutralization ApiFactory carries. See ADR 0095.
        builder.UseSetting("Telemetry:OtlpEndpoint", "");

        // The suite must behave the same on every machine: a developer's local overrides file,
        // loaded last by the host, is removed rather than out-shouted. See ADR 0035.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            foreach (var localSource in configuration.Sources
                .OfType<JsonConfigurationSource>()
                .Where(source => string.Equals(source.Path, "appsettings.Local.json", StringComparison.Ordinal))
                .ToList())
            {
                configuration.Sources.Remove(localSource);
            }

            // The suite's own key pair, out-shouting the host's settings: what the facts assert
            // on must come from this file rather than from whatever those say (ADR 0035). The
            // pair is configured by default and a fact can withhold either half — none for the
            // challenge-off state, one for the startup refusal (ADR 0083).
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Turnstile:SiteKey"] = turnstileSiteKey,
                ["Turnstile:SecretKey"] = turnstileSecretKey
            });
        });

        builder.ConfigureServices(services =>
        {
            services
                .AddHttpClient(BffEndpoints.ApiClientName)
                .ConfigurePrimaryHttpMessageHandler(() => LoginApi);

            services
                .AddHttpClient(TurnstileVerifier.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => TurnstileApi);

            services.AddSingleton<IForwarderHttpClientFactory>(
                new StubForwarderHttpClientFactory(ProxiedApi));
        });
    }

    /// <summary>
    /// A client that behaves like the browser: keeps cookies, follows no redirect, speaks HTTPS.
    /// </summary>
    /// <remarks>
    /// HTTPS is not decoration. The session cookie is <c>Secure</c> with the <c>__Host-</c> prefix,
    /// so over <c>http</c> the cookie container would drop it and every test would fail as though
    /// sign-in had not worked — which is exactly what happens to a developer who runs the
    /// application over plain HTTP, and why the <c>http</c> launch profile no longer exists.
    /// </remarks>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>
    /// An access token shaped like the one the API issues — <see cref="ClaimTypes.Name"/> included,
    /// which the serializer shortens to <c>unique_name</c> on the way out.
    /// </summary>
    public static string IssueToken(string username = "alice", TimeSpan? lifetime = null)
    {
        var token = new JwtSecurityToken(
            issuer: "tests",
            audience: "tests",
            claims:
            [
                new Claim(ClaimTypes.Name, username),
                new Claim("trainer_id", "9d1f7f2e-0e4a-4a1e-9f5f-2b3c4d5e6f70")
            ],
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(30)));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
