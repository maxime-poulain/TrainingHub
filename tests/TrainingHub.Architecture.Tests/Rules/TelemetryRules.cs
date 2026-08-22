using System.Diagnostics.Metrics;
using System.Reflection;
using TrainingHub.Architecture.Tests.Framework;
using TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator.Behaviors;
using TrainingHub.Shared.Infrastructure.Email;
using TrainingHub.Shared.Infrastructure.Outbox;
using NetArchTest.Rules;
using Xunit;

namespace TrainingHub.Architecture.Tests.Rules;

/// <summary>
/// The telemetry pipeline: one configuration, both hosts, one owner for the library, recorded
/// names, and an envelope that knows its producer.
/// </summary>
/// <remarks>
/// ADR 0095 confines OpenTelemetry to one seam the way ADR 0026 confines Serilog, and promises
/// host symmetry the same way — both promises about files and dependencies no compiled assertion
/// watched before. ADR 0096 makes the source, meter and instrument names a published contract:
/// dashboards are written against them, so a rename is an outage a build must catch. ADR 0097
/// puts one <c>traceparent</c> on the outbox envelope, which is a schema promise three files keep
/// together.
/// </remarks>
public sealed class TelemetryRules
{
    /// <summary>The composition roots the decision is about, by host.</summary>
    private static readonly string[] ApiHostPrograms =
    [
        Path.Combine("src", "DDD", "Api", "Program.cs"),
        Path.Combine("src", "DDDWithCqrs", "Api", "Program.cs"),
    ];

    /// <summary>
    /// Both api hosts, configure the same telemetry.
    /// </summary>
    /// <remarks>
    /// Read from the source, comment lines stripped, exactly like the logging rule and for the
    /// measured reason it records: a commented-out call still contains its own name. Only the
    /// registration half exists — OpenTelemetry needs no middleware, so there is no
    /// <c>UseApiTelemetry</c> to demand.
    /// </remarks>
    [Fact]
    [ArchitectureRule("0095",
        "one telemetry seam, called by both API hosts, so neither can quietly observe less than the other")]
    public void BothApiHosts_ConfigureTheSameTelemetry() =>
        ApiHostPrograms
            .Selected("API host Program.cs")
            .Where(program =>
            {
                var code = SourceTree.ReadText(Path.Combine(SourceTree.RepositoryRoot, program))
                    .Split('\n')
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
                    .ToArray();

                return !Array.Exists(code, line => line.Contains("AddApiTelemetry(", StringComparison.Ordinal));
            })
            .Select(program =>
                $"{program} never calls AddApiTelemetry. ADR 0095 wires telemetry once in Shared.Api " +
                "precisely so neither host can observe less than the other — add the call, or " +
                "record the new decision")
            .ShouldHold();

    /// <summary>
    /// Only the telemetry seam, touches open telemetry.
    /// </summary>
    /// <remarks>
    /// The same confinement shape as Serilog's and the health dashboard's, in two halves. The
    /// compiled half: no backend assembly outside the seam names an OpenTelemetry type — custom
    /// instrumentation speaks the BCL's <c>ActivitySource</c> and <c>Meter</c>, which is what
    /// makes the confinement absolute rather than aspirational. The source half: the BFF is not
    /// among the assemblies this suite loads, so its corner is read as text — only its own
    /// telemetry extension may say the word. The Serilog sink's types live under <c>Serilog</c>
    /// namespaces, so the logging extension needs no exemption here.
    /// </remarks>
    [Fact]
    [ArchitectureRule("0095",
        "custom instrumentation speaks the BCL; only the telemetry seam and the BFF's telemetry corner reference OpenTelemetry")]
    public void OnlyTheTelemetrySeam_TouchesOpenTelemetry()
    {
        // One assembly at a time, like the kernel-purity rules: a failure names the layer that
        // broke the line instead of a merged set the reader has to sort out.
        foreach (var assembly in new[]
        {
            Solution.Kernel, Solution.Domain, Solution.Application,
            Solution.LayeredApplication, Solution.CqrsApplication,
            Solution.Infrastructure, Solution.CqrsInfrastructure,
            Solution.LayeredApi, Solution.CqrsApi
        })
        {
            Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny("OpenTelemetry")
                .GetResult()
                .ShouldHold();
        }

        Types.InAssembly(Solution.SharedApi)
            .That()
            .DoNotResideInNamespace("TrainingHub.Shared.Api.Telemetry")
            .And()
            .DoNotHaveName("TelemetryExtensions")
            .Should()
            .NotHaveDependencyOnAny("OpenTelemetry")
            .GetResult()
            .ShouldHold();

        var bffTelemetryExtension = "src/Web/TrainingHub.Blazor/TrainingHub.Blazor/Telemetry/TelemetryExtensions.cs";

        SourceTree.SourceFiles
            .Select(SourceTree.Relative)
            .Where(file => file.StartsWith("src/Web/", StringComparison.Ordinal))
            .Selected("web-side source file")
            .Where(file => file != bffTelemetryExtension)
            .Where(file => SourceTree
                .ReadText(Path.Combine(SourceTree.RepositoryRoot, file.Replace('/', Path.DirectorySeparatorChar)))
                .Split('\n')
                .Select(line => line.TrimStart())
                .Any(line => !line.StartsWith("//", StringComparison.Ordinal)
                    && line.Contains("OpenTelemetry", StringComparison.Ordinal)))
            .Select(file =>
                $"'{file}' names OpenTelemetry. On the web side only the BFF's own telemetry " +
                "extension may (ADR 0095) — everything else consumes it through that corner")
            .ShouldHold();
    }

    /// <summary>
    /// The instrumentation names, are the recorded ones.
    /// </summary>
    /// <remarks>
    /// Dashboards, alerts and the seam's own subscriptions are written against these strings, so
    /// a rename is not a refactoring — it is every one of them going quietly dark. The instrument
    /// names are read from the instruments themselves, not restated constants, so the rule holds
    /// what actually runs.
    /// </remarks>
    [Fact]
    [ArchitectureRule("0096",
        "the source, meter and instrument names are a published contract, pinned to the record that names them")]
    public void TheInstrumentationNames_AreTheRecordedOnes() =>
        new (string Actual, string Recorded, string What)[]
        {
            (ApplicationTelemetry.ActivitySourceName, "TrainingHub.Application", "the application pipeline's activity source"),
            (ApplicationTelemetry.MeterName, "TrainingHub.Application", "the application pipeline's meter"),
            (OutboxTelemetry.ActivitySourceName, "TrainingHub.Outbox", "the outbox's activity source"),
            (OutboxTelemetry.MeterName, "TrainingHub.Outbox", "the outbox's meter"),
            (EmailTelemetry.ActivitySourceName, "TrainingHub.Email", "the mail adapter's activity source"),
            (EmailTelemetry.MeterName, "TrainingHub.Email", "the mail adapter's meter"),
            (InstrumentName(typeof(ApplicationTelemetry), "CommandDuration"), "traininghub.commands.duration", "the command histogram"),
            (InstrumentName(typeof(ApplicationTelemetry), "QueryDuration"), "traininghub.queries.duration", "the query histogram"),
            (InstrumentName(typeof(OutboxTelemetry), "DeliveryDuration"), "traininghub.outbox.delivery.duration", "the delivery histogram"),
            (InstrumentName(typeof(OutboxTelemetry), "Poisoned"), "traininghub.outbox.poisoned", "the poison counter"),
            (InstrumentName(typeof(OutboxTelemetry), "FactsDelivered"), "traininghub.facts.delivered", "the business-fact counter"),
            (InstrumentName(typeof(EmailTelemetry), "SendDuration"), "traininghub.email.send.duration", "the send histogram"),
        }
            .Selected("recorded instrumentation name")
            .Where(entry => entry.Actual != entry.Recorded)
            .Select(entry =>
                $"{entry.What} is named '{entry.Actual}', and ADR 0096 records '{entry.Recorded}'. " +
                "These names are what dashboards query — rename them in the record and every " +
                "consumer, or not at all")
            .ShouldHold();

    /// <summary>
    /// The inner circle, carries no telemetry package.
    /// </summary>
    /// <remarks>
    /// The compiled rules cannot see an unused package reference — the compiler prunes it — so
    /// this one reads the project files, the way the graph rules do. The kernel, the domains and
    /// the applications reference nothing named OpenTelemetry or Serilog;
    /// <c>Microsoft.Extensions.Logging.Abstractions</c> in the shared application layer stays the
    /// one recorded exception, carried since the audit handlers exist, and is not matched here.
    /// </remarks>
    [Fact]
    [ArchitectureRule("0096",
        "the inner circle carries no telemetry dependency: instrumentation there is the platform's own vocabulary or nothing")]
    public void TheInnerCircle_CarriesNoTelemetryPackage() =>
        ProjectGraph.Projects
            .Where(project => project.Name == "TrainingHub.Shared"
                || project.Name.EndsWith(".Domain", StringComparison.Ordinal)
                || project.Name.EndsWith(".Application", StringComparison.Ordinal))
            .Selected("inner-circle project")
            .SelectMany(project => project.PackageReferences
                .Where(package => package.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal)
                    || package.Name.StartsWith("Serilog", StringComparison.Ordinal))
                .Select(package =>
                    $"{project.RelativePath} references the package '{package.Name}'. The inner " +
                    "circle speaks ActivitySource and Meter — the BCL's, no package — and the seam " +
                    "subscribes (ADR 0095, ADR 0096)"))
            .ShouldHold();

    /// <summary>
    /// The outbox envelope, carries its producer's trace context.
    /// </summary>
    /// <remarks>
    /// Three files keep one schema promise, and losing any of them fails differently: without the
    /// property there is no column, without the mapping line EF silently drops a get-only
    /// property, and without the stamp every envelope is written with the column forever null —
    /// none of which any existing test would name. The two source halves are read with comment
    /// lines stripped, the lesson the logging rule measured.
    /// </remarks>
    [Fact]
    [ArchitectureRule("0097",
        "the envelope carries one traceparent, stamped by the publisher in the committing transaction, mapped like every other column")]
    public void TheOutboxEnvelope_CarriesItsProducersTraceContext()
    {
        var violations = new List<string>();

        var traceParent = Solution.Infrastructure
            .DeclaredTypes()
            .Single(type => type.Name == "OutboxMessage")
            .GetProperty("TraceParent");

        if (traceParent is null || traceParent.PropertyType != typeof(string))
        {
            violations.Add(
                "OutboxMessage no longer declares a string TraceParent property. ADR 0097 stores " +
                "the producing trace on the envelope — restore it, or record the new decision");
        }

        violations
            .Concat(new (string File, string Literal, string Meaning)[]
            {
                (Path.Combine("src", "TrainingHub.Shared.Infrastructure", "ThirdParty", "EfCore", "Outbox", "OutboxMessageConfiguration.cs"),
                    "TraceParent",
                    "the configuration no longer maps TraceParent, and a get-only property EF is not told about is silently not a column"),
                (Path.Combine("src", "TrainingHub.Shared.Infrastructure", "Outbox", "OutboxIntegrationEventPublisher.cs"),
                    "Activity.Current",
                    "the publisher no longer stamps the current activity, so every envelope commits with an empty pointer"),
            }
                .Selected("half of the envelope's promise")
                .Where(half => !SourceTree
                    .ReadText(Path.Combine(SourceTree.RepositoryRoot, half.File))
                    .Split('\n')
                    .Select(line => line.TrimStart())
                    .Any(line => !line.StartsWith("//", StringComparison.Ordinal)
                        && line.Contains(half.Literal, StringComparison.Ordinal)))
                .Select(half => $"{half.File}: {half.Meaning} (ADR 0097)"))
            .ShouldHold();
    }

    /// <summary>The name an instrument actually carries, read from the static field that holds it.</summary>
    private static string InstrumentName(Type holder, string fieldName) =>
        ((Instrument)holder
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)
            .Name;
}
