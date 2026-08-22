using TrainingHub.Blazor.Bff;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using MudBlazor.Services;
using TrainingHub.Blazor.Client.Authorization;
using TrainingHub.Blazor.Client.Infrastructure;
using TrainingHub.Blazor.Components;
using TrainingHub.Blazor.Prerendering;
using TrainingHub.Blazor.Telemetry;
using TrainingHub.GeneratedClients;
using TrainingHub.Translations;

var builder = WebApplication.CreateBuilder(args);

// The developer's own overrides, appended after every committed source and the environment, so
// they always win. Optional and git-ignored; the WebAssembly client has no configuration of its
// own, so this host is the only Blazor-side place an override — the API base address, say — can
// live. See ADR 0035.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Traces, metrics and logs over OTLP, off entirely while the Telemetry section names no endpoint.
// The BFF's own counterpart of the API hosts' seam — see the extension for why this host must be
// traced for any trace to be whole (ADR 0095).
builder.Services.AddBffTelemetry(builder.Configuration, builder.Environment);

// Add MudBlazor services
builder.Services.AddMudServices();

// The browser's services are deliberately NOT registered here. AddDependencies describes the
// WebAssembly application's world — an HttpClient pointed at the page's own origin, an identity
// read from /bff/user — and this host is the other side of both. What registering them did do was
// collide with this host's own API client, which shares the name "Api", and answer sign-in with a
// 500. The BFF suite catches that.
//
// The catalog's routes prerender (see App.razor), so the services those pages and the layout
// inject get server-side counterparts of their own here — each the host's honest answer to the
// same question, never the browser's registration adopted (ADR 0072):
//
//  - identity is the cookie the BFF pipeline already authenticated, read in place rather than
//    fetched from /bff/user by this host calling itself;
//  - the generated read clients ride the BFF's own named client to the API, under its name and
//    never under "Api";
//  - the session client and the standing source resolve because instantiation must, and behave
//    honestly if a prerender ever reaches them — the one signs out nobody, the other answers
//    "active" to a caller the API refuses.
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
// The prerendered layout injects IStringLocalizer, so this host resolves it too — over the same
// resource assembly the browser carries, because the two passes must say the same words (ADR 0088).
builder.Services.AddLocalization();
// This host answers people, so the visitor's explicit choice comes first: the standard culture
// cookie — written by /bff/culture, nothing else writes one — then the browser's Accept-Language,
// then English. The API hosts read the header alone; the proxy restates the resolution there, so
// the three legs cannot disagree (ADR 0088).
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(SupportedLanguages.Default);
    options.AddSupportedCultures([.. SupportedLanguages.All]);
    options.AddSupportedUICultures([.. SupportedLanguages.All]);
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});
// The same policy the browser registers, because the same components render on both sides of the
// prerender. Left out here, a page carrying it would be refused during the prerendered pass and
// granted a moment later — which reads as a flicker and is really two hosts disagreeing about who
// the caller is (ADR 0078).
builder.Services.AddAuthorizationCore(options => options.AddPolicy(
    SessionPolicies.Trainer,
    policy => policy.RequireClaim(SessionClaims.TrainerId)));
builder.Services.AddScoped<AuthenticationStateProvider, HostAuthenticationStateProvider>();
builder.Services.AddScoped<IAuthenticationStateNotifier>(serviceProvider =>
    (HostAuthenticationStateProvider)serviceProvider.GetRequiredService<AuthenticationStateProvider>());
builder.Services.AddScoped<IBffSessionClient, BffSessionClient>();
builder.Services.AddScoped<IBffCultureClient, BffCultureClient>();
builder.Services.AddScoped<TrainerStandingSource>();
builder.Services.AddScoped<ITrainerStandingSource>(serviceProvider =>
    serviceProvider.GetRequiredService<TrainerStandingSource>());
builder.Services.AddScoped<ITrainerPortraitSource>(serviceProvider =>
    serviceProvider.GetRequiredService<TrainerStandingSource>());
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(BffEndpoints.ApiClientName);
builder.Services.AddHttpClient<ITrainerClient, TrainerClient>(BffEndpoints.ApiClientName);
// Resolved because the catalog's pages inject it and they prerender here; never used during the
// prerendered pass, because the dialog that deposits a token only opens interactively (ADR 0083).
builder.Services.AddSingleton<TurnstileTokenAccessor>();

// This host is the backend for frontend. It terminates authentication, keeps the API's access
// token in a cookie the browser cannot read, and forwards everything under /api to the API with
// that token attached. The WebAssembly application therefore talks only to its own origin and
// never holds a credential.
//
// It used to authenticate nobody: the browser kept a JWT in localStorage and called the API
// cross-origin, which put a portable credential within reach of any script running in the page.
// See ADR 0009.
builder.Services.AddBff(builder.Configuration);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();

// Liveness only, and the framework calls inline rather than the shared AddApiHealth pair: that
// pair wires probes for a database, an object store and a mail server, and this host owns none of
// them. Readiness is not proxied either — this host's world is the API, and answering for it would
// be a decision of its own. See ADR 0037, and ADR 0076 for the target framework that used to be
// given as the reason and never was one.
builder.Services.AddHealthChecks();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Before the BFF, because the culture has to be a fact before anything renders or is forwarded:
// the prerendered pass reads what is resolved here, and the proxy's transform repeats it to the
// API. The cookie is the visitor's explicit choice; Accept-Language decides for everyone who
// never chose; English otherwise (ADR 0088).
app.UseRequestLocalization();

// Before UseAntiforgery, because it is what authenticates the request: antiforgery binds its
// token to the signed-in user, and a pipeline that validated before knowing who the caller is
// would be validating against nobody.
app.UseBff();

app.UseAntiforgery();

// A 200 here means this process is up and routing — all a container restart decision should read.
app.MapHealthChecks("/health/live");

// Beside the health checks and outside the BFF's own group, because the audience is neither the
// browser nor an operator: robots.txt, the sitemap and the portrait pass-throughs answer the
// crawlers ADR 0072 started prerendering for (ADR 0073).
app.MapCrawlerEndpoints();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(TrainingHub.Blazor.Client._Imports).Assembly);

app.Run();

/// <summary>
/// Named so the BFF suite can host this application. Top-level statements compile to an internal
/// <c>Program</c>, which <c>WebApplicationFactory&lt;T&gt;</c> cannot reach.
/// </summary>
public sealed partial class Program { }
