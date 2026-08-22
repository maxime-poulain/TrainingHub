using TrainingHub.DDDWithCqrs.Application.Features.Trainers.Create;
using TrainingHub.Shared.Application.EventHandlers;
using TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator;
using TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator.Behaviors;
using TrainingHub.Shared.Api.Extensions;
using TrainingHub.Shared.CQS;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate.DomainEvents;
using TrainingHub.Shared.Infrastructure.Extensions;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

// The developer's own overrides, appended after every committed source, the user secrets and the
// environment, so they always win. Optional, git-ignored and excluded from the Docker build
// context: in a public repository this file is where a local connection string or a real SMTP
// key is allowed to exist. See ADR 0035.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Add services to the container.

// The contract's own refusals answer in the resolved culture: the data annotation templates
// are looked up in the validation family by their English text, which is why an English answer
// never moves (ADR 0089).
builder.Services.AddControllers().AddApiValidationLocalization();

// Shared with the layered host so the two cannot drift: a policy defined in one Program.cs only
// protects that host. See CorsExtensions for why the origins come from configuration.
builder.Services.AddApiCors(builder.Configuration);

// One error format for both hosts: RFC 7807 ProblemDetails, whatever failed.
builder.Services.AddApiProblemDetails();

// One logging pipeline for both hosts: Serilog, console and rolling files, tuned by the
// ApiLogging section. Declared in Shared.Api so neither host can quietly log less than the other.
builder.Services.AddApiLogging(builder.Configuration);

// One telemetry pipeline for both hosts: traces, metrics and logs over OTLP, tuned by the
// Telemetry section and off entirely while it names no endpoint. This host adds the source its
// pipeline behavior speaks from, so command and query spans are recorded too. See ADR 0095.
builder.Services.AddApiTelemetry(builder.Configuration, builder.Environment, ApplicationTelemetry.ActivitySourceName);

// One language policy for both hosts: Accept-Language against the supported set, English
// otherwise. The BFF says the visitor's cookie choice through that same header. See ADR 0088.
builder.Services.AddApiLocalization();

// The framework's OpenAPI generator, shared with the layered host. This one used to call a bare
// AddSwaggerGen(), so its document declared no security scheme and no authenticated endpoint could
// be tried from its UI. See ADR 0006.
builder.Services.AddApiOpenApi();

// The five readiness probes — database, object store, mail relay, outbox poison, pending
// migrations — shared with the layered host so neither can answer less than the other. See ADR 0037
// for the pair of endpoints, ADR 0045 for the schema.
builder.Services.AddApiHealth();

// The dashboard over those probes. Self-gating: outside Development this is a no-op, so the call
// stays unconditional here and production never grows a control room.
builder.Services.AddApiHealthDashboard(builder.Environment);

builder.Services.AddMediator(configuration =>
{
    configuration.Assemblies =
    [
        typeof(CreateTrainerCommand).Assembly,      // DDDWithCqrs.Application: commands + command handlers
        typeof(TrainerCreatedDomainEvent).Assembly, // Shared.Domain: domain events
        typeof(MediatorCommandDispatcher).Assembly, // DDDWithCqrs.Infrastructure: query handlers
        typeof(PublishIntegrationEventWhenTrainerCreatedDomainEventHandler).Assembly // Shared.Application: domain event handlers
    ];
    // Telemetry first, so its span wraps validation and a rejected command counts as a failed
    // command — the same statement ADR 0016 makes about the HTTP answer (ADR 0096).
    configuration.PipelineBehaviors = [typeof(TelemetryPipelineBehavior<,>), typeof(ValidationPipelineBehavior<,>), typeof(NoTrackingDuringQueryExecutionBehavior<,>)];
    configuration.ServiceLifetime = ServiceLifetime.Transient;
});

builder.Services.AddTransient<ICommandDispatcher, MediatorCommandDispatcher>();
builder.Services.AddTransient<IQueryDispatcher, MediatorQueryDispatcher>();

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddValidatorsFromAssembly(typeof(CreateTrainerCommandValidator).Assembly);

// Identity, JWT validation and the three authorization policies are the same on both hosts, and
// are declared once in TrainingHub.Shared.Api so neither can quietly lose a rule the other keeps.
builder.Services.AddApiIdentity(builder.Configuration);
builder.Services.AddJwtBearerAuthentication(builder.Configuration);
builder.Services.AddApiAuthorization();

// The two windows this host enforces: how much mail one trainer can be made to receive,
// partitioned by the recipient because behind the proxy every caller wears the same address
// (ADR 0082), and how often one account can ask for its verification email, partitioned by the
// authenticated caller because on that route the identity is real (ADR 0090).
builder.Services.AddRateLimiter(options =>
{
    ContactRateLimiting.Configure(options);
    VerificationRateLimiting.Configure(options);
});

var app = builder.Build();

// Configure the HTTP request pipeline.

// First, so everything downstream is covered: authentication, authorization, routing and the
// actions alike. The handlers themselves, and the order they are tried in, are declared once in
// Shared.Api and shared with the layered host — which had no exception handling at all.
app.UseApiExceptionHandling();

// One line per request — the ApiLogging defaults silence the framework's own narration.
app.UseApiLogging();

if (app.Environment.IsDevelopment())
{
    app.UseApiOpenApi();
}

app.UseHttpsRedirection();

// Before authentication on purpose: an unauthenticated cross-origin call must still be answered
// with the CORS headers, or the browser reports a CORS failure instead of the 401 it received.
app.UseApiCors();

// Before authentication, because the culture is a fact about the request rather than about the
// caller: everything downstream — the validators' sentences first — reads what is resolved here.
app.UseApiLocalization();

app.UseAuthentication();
app.UseAuthorization();

// After authorization, so a refused caller is refused before a window is spent on them.
app.UseRateLimiter();

app.MapControllers();

// /health/live and /health/ready, both anonymous: an orchestrator holds no token, and the body
// names statuses and nothing else. See ADR 0037.
app.MapApiHealth();

// /healthchecks-ui and the detailed endpoint it reads — Development only, like Scalar; the gate
// lives inside the extension.
app.MapApiHealthDashboard();

await app.EnsureDatabasesAreUpToDateAsync();

// After the migrations, because the role is a row: Development only, and a no-op unless a
// developer names their own account in appsettings.Local.json. See ADR 0051 — there is no endpoint
// that grants a role, on either host.
await app.EnsureAdministratorAsync();

// And after the administrator, because the catalog is what an administrator is there to look at.
// Development only, and a no-op unless DevelopmentData:Enabled says otherwise — five hundred
// trainings is not something to discover on a machine that was only supposed to start (ADR 0079).
await app.EnsureDevelopmentCatalogAsync();

app.Run();

/// <summary>
/// The entry point of the CQRS host.
/// </summary>
/// <remarks>
/// Declared explicitly, rather than left to the compiler, so the integration tests can
/// name it as the type argument to <c>WebApplicationFactory</c>.
/// </remarks>
public sealed partial class Program { }
