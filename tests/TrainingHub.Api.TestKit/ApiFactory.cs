using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using TrainingHub.Shared.Application.IntegrationEvents;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Interceptors;
using TrainingHub.Shared.Infrastructure.ThirdParty.Identity;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Respawn;
using Respawn.Graph;
using Testcontainers.MsSql;
using Xunit;

namespace TrainingHub.Api.TestKit;

/// <summary>
/// Hosts an API against a real SQL Server started by Testcontainers, and empties it between
/// tests. Generic over the entry point so both stacks share one implementation: the two
/// <see cref="DbContext"/> it replaces live in the shared infrastructure and are common to
/// both hosts, leaving <typeparamref name="TEntryPoint"/> as the only difference.
/// </summary>
public abstract class ApiFactory<TEntryPoint>
    : WebApplicationFactory<TEntryPoint>, IAsyncLifetime, IResettableDatabase, IServiceScopeSource,
      IHttpClientSource, IServerErrorSource, ILogFileSource, IMailboxSource
    where TEntryPoint : class
{
    /// <inheritdoc />
    /// <remarks>
    /// Owned by this fixture and deleted with it, which is what makes pointing the file sink at a
    /// real directory acceptable: two fixtures can never race for one relative path, and the
    /// sensitive data Development logging writes does not outlive the run.
    /// </remarks>
    public string LogDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "traininghub-tests", Guid.NewGuid().ToString("N"));

    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>The suite's own database on that container, rather than the container's default.</summary>
    /// <remarks>
    /// The MsSql module's connection string names <c>master</c>, and the suites ran there for a
    /// long time with nothing saying so. ADR 0094's migration is what finally objected:
    /// <c>ALTER DATABASE CURRENT</c> refuses a system database — the right refusal, because an
    /// application schema does not belong in <c>master</c> and <c>READ_COMMITTED_SNAPSHOT</c>
    /// cannot even be turned on there. Only the consumed string changes: the container still
    /// answers its readiness probe on <c>master</c>, and the hosts migrate this database into
    /// existence on startup, exactly as they would a real one.
    /// </remarks>
    private string SuiteConnectionString =>
        new SqlConnectionStringBuilder(_msSqlContainer.GetConnectionString())
        {
            InitialCatalog = "TrainingHub",
        }.ConnectionString;

    /// <summary>
    /// The object store the suite runs against.
    /// </summary>
    /// <remarks>
    /// A real S3 server rather than a fake, because what the photo endpoints are worth proving is
    /// exactly what a fake would assume: that bytes written under one key come back under it, that
    /// a missing key answers rather than throws, and that a replaced photo really does stop being
    /// there. SeaweedFS ships no Testcontainers module, so the image is driven directly.
    /// </remarks>
    private readonly IContainer _objectStoreContainer = new ContainerBuilder("chrislusf/seaweedfs:4.40")
        .WithCommand("server", "-s3", "-dir=/data", "-s3.config=" + S3ConfigPath)
        // Without an identity file SeaweedFS is not permissive, which is exactly the trap: it
        // allows anonymous requests and refuses signed ones outright — "Signed request requires
        // setting up SeaweedFS S3 authentication". Every AWS SDK signs every request, so the store
        // answers 500 to all of them while looking entirely healthy from the outside, and the
        // message never reaches the client because a 500 is published without its cause.
        //
        // Declaring an identity also moves this closer to production rather than further from it:
        // requests are signed and the signature is verified, as a hosted bucket would.
        .WithResourceMapping(Encoding.UTF8.GetBytes(S3Config), S3ConfigPath)
        .WithPortBinding(SeaweedS3Port, assignRandomHostPort: true)
        .WithPortBinding(SeaweedMasterPort, assignRandomHostPort: true)
        // Two probes, because "the store is up" and "the store can accept a write" are different
        // facts and only the second one matters.
        //
        // The S3 port answering HTTP says the gateway is listening. It says nothing about whether
        // the volume server behind it has registered with the master — and until it has, the
        // master cannot allocate anywhere to put bytes. Creating a bucket still works, because
        // that is metadata; the first PutObject answers 500. So the second probe asks the master
        // to assign a file id, which is precisely what a write needs and the only question whose
        // answer is the readiness being waited for. The assigned id is thrown away.
        //
        // The status matcher on the first is not laziness either: an unsigned request to an S3
        // root is answered 403, so a strategy demanding 2xx would wait forever on a healthy
        // container.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request
                .ForPath("/")
                .ForPort(SeaweedS3Port)
                .ForStatusCodeMatching(status => (int)status < 500))
            .UntilHttpRequestIsSucceeded(request => request
                .ForPath("/dir/assign")
                .ForPort(SeaweedMasterPort)
                .ForStatusCode(HttpStatusCode.OK)))
        .Build();

    /// <summary>
    /// The mail server the suite delivers through.
    /// </summary>
    /// <remarks>
    /// A real SMTP server rather than a substituted port, because what the email path is worth
    /// proving is exactly what a substitute would assume: that a message composed by a handler
    /// leaves the host over the wire and arrives addressed, titled and worded as intended. Mailpit
    /// ships no Testcontainers module either, so the image is driven directly — same tag as the
    /// compose stack, no shared configuration (it needs none).
    /// </remarks>
    private readonly IContainer _mailContainer = new ContainerBuilder("axllent/mailpit:v1.30.6")
        .WithPortBinding(MailpitSmtpPort, assignRandomHostPort: true)
        .WithPortBinding(MailpitHttpPort, assignRandomHostPort: true)
        // One probe where SeaweedFS needs two: /readyz answers only once both listeners are up,
        // and there is no later registration step for a write to trip over.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(request => request
                .ForPath("/readyz")
                .ForPort(MailpitHttpPort)
                .ForStatusCode(HttpStatusCode.OK)))
        .Build();

    private const ushort MailpitSmtpPort = 1025;

    private const ushort MailpitHttpPort = 8025;

    /// <inheritdoc />
    public Uri MailboxApiBaseAddress =>
        new($"http://{_mailContainer.Hostname}:{_mailContainer.GetMappedPublicPort(MailpitHttpPort)}");

    private const ushort SeaweedS3Port = 8333;

    private const ushort SeaweedMasterPort = 9333;

    private const string S3ConfigPath = "/etc/seaweedfs/s3.json";

    /// <summary>
    /// The identity this run uses, minted rather than written down.
    /// </summary>
    /// <remarks>
    /// Generated for two reasons, and only one of them is that a literal secret in a repository is
    /// a finding waiting to happen. The other is that it cannot drift: the value the container will
    /// accept and the value the host is told to send come from the same expression, so there is no
    /// way to change one and leave the other. A pair of constants was one edit away from a failure
    /// that would have read as a broken adapter.
    /// </remarks>
    private static readonly string ObjectStoreAccessKey = "key-" + Guid.NewGuid().ToString("N");

    private static readonly string ObjectStoreSecretKey = "secret-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The identity file the container is started with, built from the credentials above.
    /// </summary>
    /// <remarks>
    /// Not read from <c>docker/seaweedfs-s3.json</c>, which serves the compose stack: that file has
    /// its own credentials and its own lifetime, and a suite quietly depending on it would break
    /// the day somebody rotated them for a reason that had nothing to do with tests.
    /// </remarks>
    private static readonly string S3Config = $$"""
        {
          "identities": [
            {
              "name": "integration-tests",
              "credentials": [{ "accessKey": "{{ObjectStoreAccessKey}}", "secretKey": "{{ObjectStoreSecretKey}}" }],
              "actions": ["Admin", "Read", "Write", "List", "Tagging"]
            }
          ]
        }
        """;

    /// <summary>
    /// The bucket the suite writes to.
    /// </summary>
    protected const string BucketName = "blrefactoring-tests";

    private readonly ConcurrentQueue<string> _serverErrors = new();

    /// <inheritdoc />
    public IReadOnlyList<string> ServerErrors => [.. _serverErrors];

    private Respawner _respawner = null!;

    // Respawn works against an open DbConnection; one is kept for the lifetime of
    // the fixture rather than reopened per reset.
    private DbConnection _connection = null!;

    /// <summary>
    /// Configure web host.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // The interceptors are re-attached deliberately. Replacing the options
            // drops the ones AddInfrastructure had configured, and without them no
            // domain event is dispatched and the audit columns stay unstamped —
            // the suite would exercise a different pipeline than production and
            // pass for the wrong reasons.
            ReplaceDbContext<TrainingContext>(services, (serviceProvider, options) =>
                options.AddInterceptors(
                    serviceProvider.GetRequiredService<DomainEventInterceptor>(),
                    serviceProvider.GetRequiredService<AuditableEntitiesInterceptor>()));

            ReplaceDbContext<TrainingIdentityDbContext>(services);

            // The failing neighbor production does not have: appended after AddInfrastructure's
            // registrations, so it runs after the welcome-email consumer — the interleaving the
            // per-consumer isolation proof needs (ADR 0034). A singleton, so its once-only memory
            // survives the worker's per-batch scopes; it acts only on marked registrations.
            services.AddSingleton<IIntegrationEventHandler<TrainerCreatedIntegrationEvent>,
                FailOnceWhenTrainerCreatedIntegrationEventHandler>();
        });

        // The suites must behave the same on every machine, and a developer's
        // appsettings.Local.json — which Program.cs loads last and this factory's content root
        // can see — would quietly reshape them: CorsTest asserts the exact origins the
        // Development file declares, and Jwt has no override here at all. The source is removed
        // rather than out-shouted, because out-shouting every key it might carry is a race this
        // factory cannot win. See ADR 0035.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            foreach (var localSource in configuration.Sources
                .OfType<JsonConfigurationSource>()
                .Where(source => string.Equals(source.Path, "appsettings.Local.json", StringComparison.Ordinal))
                .ToList())
            {
                configuration.Sources.Remove(localSource);
            }
        });

        // Pointed at the container rather than the address in appsettings, and left otherwise
        // untouched: the S3 adapter, the bucket bootstrapper and the key layout are all the
        // production ones, which is the only way this suite proves anything about them.
        builder.UseSetting(
            "ObjectStorage:ServiceUrl",
            $"http://{_objectStoreContainer.Hostname}:{_objectStoreContainer.GetMappedPublicPort(SeaweedS3Port)}");
        builder.UseSetting("ObjectStorage:BucketName", BucketName);
        builder.UseSetting("ObjectStorage:AccessKey", ObjectStoreAccessKey);
        builder.UseSetting("ObjectStorage:SecretKey", ObjectStoreSecretKey);
        builder.UseSetting("ObjectStorage:CreateBucketOnStartup", "true");

        // Pointed at the mail container the same way, and left otherwise untouched: the MailKit
        // adapter, its options validation and the sender identity are the production ones. The
        // sender address differs from appsettings so a message that ignored its configuration
        // would be visible, not coincidentally correct.
        builder.UseSetting("Smtp:Host", _mailContainer.Hostname);
        builder.UseSetting(
            "Smtp:Port",
            _mailContainer.GetMappedPublicPort(MailpitSmtpPort).ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("Smtp:SenderAddress", "no-reply@traininghub.test");
        builder.UseSetting("Smtp:SenderName", "TrainingHub Tests");

        // The outbox worker's cadence, shrunk for the suite: the delivery proofs wait on real
        // polling, and five seconds per assertion is a tax every test would pay. The attempt
        // budget shrinks with it, so the poison proof can spend it within a test's patience.
        builder.UseSetting("Outbox:PollInterval", "00:00:00.250");
        builder.UseSetting("Outbox:MaxAttempts", "2");

        // The retry schedule and the retention shrink with the cadence: a hundred milliseconds of
        // backoff keeps the poison proof inside a test's patience, and a thirty-second retention
        // sweeps a planted stale row at once while every freshly delivered row comfortably
        // outlives its observation window.
        builder.UseSetting("Outbox:RetryDelay", "00:00:00.100");
        builder.UseSetting("Outbox:RetentionPeriod", "00:00:30");

        // The file sink stays on, pointed at a directory this fixture owns — see LogDirectory for
        // why that is safe. Kept real rather than disabled because the file is the one place a
        // test can read what the pipeline actually wrote, properties and template included: the
        // recording provider only keeps formatted error messages, so LoggingTest's enrichment
        // proofs read the sink itself.
        builder.UseSetting("ApiLogging:Path", Path.Combine(LogDirectory, "traininghub-.log"));

        // The host runs in this process, so what it logs while failing is readable from a test —
        // and a 500 says nothing on purpose. Without this an assertion reports the status and the
        // cause stays in a stream nobody reads.
        builder.ConfigureLogging(logging => logging.AddProvider(new RecordingLoggerProvider(_serverErrors)));

        // The Development file names the local dashboard's OTLP endpoint, and this factory runs
        // under Development; blanked so no suite ever opens an exporter toward a collector that
        // is not there. Blank is the off switch the option documents — which is why it is a
        // string and not a Uri. See ADR 0095.
        builder.UseSetting("Telemetry:OtlpEndpoint", "");

        builder.UseEnvironment("Development");
    }

    private void ReplaceDbContext<TContext>(
        IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<TContext>));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }

        // Only DbContextOptions<TContext> is removed, so the interceptor
        // registrations made by AddInfrastructure survive and stay resolvable.
        services.AddDbContext<TContext>((serviceProvider, options) =>
        {
            options.UseSqlServer(SuiteConnectionString);
            configure?.Invoke(serviceProvider, options);
        });
    }

    /// <summary>
    /// Initialize async.
    /// </summary>
    public async Task InitializeAsync()
    {
        // All in parallel: none knows about the others, and starting a database, an object store
        // and a mail server one after the other triples the wait every suite pays before its
        // first assertion.
        await Task.WhenAll(
            _msSqlContainer.StartAsync(),
            _objectStoreContainer.StartAsync(),
            _mailContainer.StartAsync());

        // Everything after the container has started is wrapped, because xUnit does not call
        // DisposeAsync on a fixture whose initialization threw. A failed migration would then
        // leave a SQL Server container running with nothing left holding a reference to it —
        // reaped by Ryuk eventually, and not at all on a machine where Ryuk is disabled.
        try
        {
            // Materialize the host now rather than on the first CreateClient(): its
            // startup is what applies the migrations, and Respawn reads the schema when
            // the checkpoint is created. Creating it against an empty database would
            // yield a checkpoint that resets nothing — silently, which is worse than
            // no reset at all.
            using (var _ = CreateClient())
            {
            }

            _connection = new SqlConnection(SuiteConnectionString);
            await _connection.OpenAsync();

            _respawner = await Respawner.CreateAsync(
                _connection,
                new RespawnerOptions
                {
                    DbAdapter = DbAdapter.SqlServer,
                    SchemasToInclude = ["dbo"],
                    // Both DbContexts write their applied migrations here. Wiping it
                    // would leave the database's data inconsistent with its schema.
                    TablesToIgnore = [new Table("__EFMigrationsHistory")]
                });
        }
        catch
        {
            await _msSqlContainer.DisposeAsync();
            await _objectStoreContainer.DisposeAsync();
            await _mailContainer.DisposeAsync();
            throw;
        }
    }

    /// <summary>SQL Server's error number for a transaction chosen as a deadlock victim.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// Empties every table but the migration history, so a test starts from a known
    /// state instead of inheriting whatever its predecessors left behind.
    /// </summary>
    /// <remarks>
    /// Retried when SQL Server picks it as a deadlock victim, and only then. The host's outbox
    /// delivery worker is a real background loop (ADR 0025) leasing, delivering and sweeping rows
    /// while this empties the tables, and once in a long while the two collide; the error's own
    /// text says what to do — "Rerun the transaction" — and a moment later the sweep it collided
    /// with has committed. Anything broader than 1205 is rethrown at once: a retry that swallowed
    /// real failures would turn a broken fixture into a slow, green one.
    /// </remarks>
    public async Task ResetDatabaseAsync()
    {
        const int Attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _respawner.ResetAsync(_connection);
                return;
            }
            catch (SqlException exception)
                when (exception.Number == DeadlockVictim && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <inheritdoc />
    public IServiceScope CreateScope() => Services.CreateScope();

    /// <remarks>
    /// <c>DisposeAsync</c> on the container, not <c>StopAsync</c>: stopping leaves the container in
    /// place, so a run that is interrupted — or a machine with <c>TESTCONTAINERS_RYUK_DISABLED</c>
    /// set — accumulates stopped SQL Servers. The connection is null-guarded because initialization
    /// can fail before it exists.
    /// </remarks>
    public new async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _msSqlContainer.DisposeAsync();
        await _objectStoreContainer.DisposeAsync();
        await _mailContainer.DisposeAsync();
        await base.DisposeAsync();

        // After the host is gone, so the file sink has released its handle. Best-effort on
        // purpose: a leftover temp directory is not worth failing a green run over.
        try
        {
            if (Directory.Exists(LogDirectory))
            {
                Directory.Delete(LogDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
