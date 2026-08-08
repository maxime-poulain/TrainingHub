using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Tests.Helpers;
using TrainingHub.Shared.Infrastructure.Queries;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using Xunit;

namespace TrainingHub.Shared.Infrastructure.Tests.Queries;

/// <summary>
/// The names the administrative listing of trainings needs and no aggregate carries.
/// </summary>
/// <remarks>
/// A training knows its owner's identifier and has never known their name, so the
/// layered reader asks this port once per page and folds the answers into rows it has already
/// mapped. Against a database rather than a substitute, because the risk is the translation: the
/// column is value-converted, and a set compared as <see cref="Guid"/> rather than as
/// <see cref="TrainerId"/> throws at translation time instead of filtering. The in-memory provider
/// would answer the same question in LINQ to objects and prove none of that — it cannot even query
/// a complex property (ADR 0032).
/// </remarks>
public sealed class TrainerNamesQueryTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private TrainingContext _context = null!;

    /// <summary>Opens the connection the database lives inside, and builds the schema on it.</summary>
    /// <remarks>
    /// The database exists for exactly as long as this connection is open, which is what keeps one
    /// test's trainers out of another's. The schema comes from the model rather than the migrations:
    /// those are scaffolded for SQL Server.
    /// </remarks>
    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();

        _context = new TrainingContext(new DbContextOptionsBuilder<TrainingContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, RowVersionWrittenByTheTest>()
            .Options);

        await _context.Database.EnsureCreatedAsync();
    }

    /// <summary>Closes the context and, with the connection, the database itself.</summary>
    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Get async, a page of identifiers, names every one of them.
    /// </summary>
    /// <remarks>
    /// The whole page in one call, keyed by the identifier the caller already has on the row rather
    /// than by whatever order the database answered in.
    /// </remarks>
    [Fact]
    public async Task GetAsync_APageOfIdentifiers_NamesEveryOneOfThem()
    {
        var ada = Trainer("Ada", "Lovelace");
        var alan = Trainer("Alan", "Turing");

        await AddAsync(ada, alan);

        var names = await new TrainerNamesQuery(_context).GetAsync([ada.Id.Value, alan.Id.Value]);

        names.Should().HaveCount(2);
        names[ada.Id.Value].Should().Be("Ada Lovelace");
        names[alan.Id.Value].Should().Be("Alan Turing");
    }

    /// <summary>
    /// Get async, an identifier nobody answers to, leaves it out.
    /// </summary>
    /// <remarks>
    /// The caller reads this with <c>GetValueOrDefault</c> and shows "no longer a trainer" for what
    /// is missing. An entry mapping the identifier to an empty name would defeat that: the row would
    /// claim a name and display none.
    /// </remarks>
    [Fact]
    public async Task GetAsync_AnIdentifierNobodyAnswersTo_LeavesItOut()
    {
        var ada = Trainer("Ada", "Lovelace");

        await AddAsync(ada);

        var gone = Guid.NewGuid();

        var names = await new TrainerNamesQuery(_context).GetAsync([ada.Id.Value, gone]);

        names.Should().ContainKey(ada.Id.Value);
        names.Should().NotContainKey(gone);
    }

    /// <summary>
    /// Get async, a repeated identifier, answers about it once.
    /// </summary>
    /// <remarks>
    /// A page where one trainer owns several trainings is the ordinary case rather than the exotic
    /// one, and a dictionary built from a sequence holding the same key twice throws.
    /// </remarks>
    [Fact]
    public async Task GetAsync_ARepeatedIdentifier_AnswersAboutItOnce()
    {
        var ada = Trainer("Ada", "Lovelace");

        await AddAsync(ada);

        var names = await new TrainerNamesQuery(_context)
            .GetAsync([ada.Id.Value, ada.Id.Value, ada.Id.Value]);

        names.Should().ContainSingle().Which.Value.Should().Be("Ada Lovelace");
    }

    /// <summary>
    /// Get async, no identifier at all, answers without asking the database.
    /// </summary>
    /// <remarks>
    /// A page holding no training asks about no owner, and the unconnected context is what makes the
    /// assertion say so: with the short circuit gone this opens a connection nothing answers, and
    /// fails instead of returning an empty dictionary.
    /// </remarks>
    [Fact]
    public async Task GetAsync_NoIdentifierAtAll_AnswersWithoutAskingTheDatabase()
    {
        await using var unconnected = new TrainingContext(
            new DbContextOptionsBuilder<TrainingContext>()
                // A syntactically valid connection string nothing will ever open.
                .UseSqlServer("Server=unreachable;Database=unused;Integrated Security=false;User Id=nobody;Password=nothing;TrustServerCertificate=true")
                .Options);

        var names = await new TrainerNamesQuery(unconnected).GetAsync([]);

        names.Should().BeEmpty();
    }

    private static Trainer Trainer(string firstname, string lastname) =>
        new TrainerBuilder()
            .WithFirstname(firstname)
            .WithLastname(lastname)
            .WithContactEmail($"{firstname}.{lastname}@example.com")
            .Build();

    private async Task AddAsync(params Trainer[] trainers)
    {
        _context.Trainers.AddRange(trainers);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Hands the concurrency token back to the application, for this suite only.
    /// </summary>
    /// <remarks>
    /// <c>IsRowVersion</c> names a column SQL Server writes by itself, so EF leaves it out of every
    /// insert. SQLite has no such column and no such generator, so the insert would offer nothing
    /// for a column declared <c>NOT NULL</c>. Saying the value is never store-generated makes EF
    /// send the empty array the aggregate already carries — which is all this suite needs, since it
    /// asks a question and never competes for a row.
    /// </remarks>
    private sealed class RowVersionWrittenByTheTest(ModelCustomizerDependencies dependencies)
        : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);

            ArgumentNullException.ThrowIfNull(modelBuilder);

            var rowVersions = modelBuilder.Model.GetEntityTypes()
                .Select(entity => entity.FindProperty("RowVersion"))
                .OfType<IMutableProperty>();

            foreach (var rowVersion in rowVersions)
            {
                rowVersion.ValueGenerated = ValueGenerated.Never;
            }
        }
    }
}
