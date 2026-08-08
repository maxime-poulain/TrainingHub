using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using TrainingHub.Shared.Api.Contracts.Administration;
using TrainingHub.Shared.Api.Contracts.Pagination;
using TrainingHub.Shared.Api.Contracts.Trainers;
using TrainingHub.Shared.Api.Contracts.Trainings;
using Xunit;

namespace TrainingHub.Api.TestKit;

/// <summary>
/// The two administrative listings over HTTP, on both hosts (ADR 0055).
/// </summary>
/// <remarks>
/// The mapping, the criteria and the envelope are each proven where they live. What only this suite
/// can prove is what they add up to against a real database: that the filter reaches SQL rather than
/// a materialised page, that the administration can find what it took down, and that nobody else can
/// ask at all.
/// </remarks>
/// <typeparam name="TFactory">The suite's fixture.</typeparam>
public abstract class AdministrationListTest<TFactory>(TFactory factory) : IntegrationTest<TFactory>(factory)
    where TFactory : IResettableDatabase, IHttpClientSource, IServiceScopeSource
{
    /// <summary>
    /// A withheld training, is findable by state and carries the reason it was taken down for.
    /// </summary>
    /// <remarks>
    /// The listing's reason for existing: before it, an administrator could withhold a training and
    /// then had no way of finding it again. The reason travels with the row, which is the one column
    /// this API publishes nowhere else.
    /// </remarks>
    [Fact]
    public async Task AWithheldTraining_IsFindableByStateAndCarriesItsReason()
    {
        var owner = await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);
        var kept = await CreateTrainingAsync(owner, "A training that stays on offer");
        var taken = await CreateTrainingAsync(owner, "A training the administration takes down");

        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        (await administrator.PostAsJsonAsync(
            $"/Administration/trainings/{taken}/withhold",
            new WithholdTrainingHttpRequest { Reason = "Reported for misleading claims." }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var page = await ReadTrainingsAsync(administrator, "?status=Withheld");

        var row = page.Items.Should().ContainSingle(
            "only one of the two was withheld").Subject;

        row.Id.Should().Be(taken);
        row.WithholdingReason.Should().Be("Reported for misleading claims.");
        row.TrainerId.Should().NotBeEmpty();
        page.Items.Should().NotContain(item => item.Id == kept);
    }

    /// <summary>
    /// A training row, names its owner rather than only identifying them.
    /// </summary>
    /// <remarks>
    /// The one column the two stacks obtain differently and must still agree on: the CQRS handler
    /// joins in its projection, the layered service asks a second question and folds the answers in
    /// (ADR 0044 leaves the row built once either way). A join that silently produced nothing would
    /// show an administrator a screen of blanks, and a unit test of either stack alone would not
    /// notice — this is the only place both are asked the same question against a real database.
    /// </remarks>
    [Fact]
    public async Task ATrainingRow_NamesItsOwnerRatherThanOnlyIdentifyingThem()
    {
        var owner = await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);
        var profile = await CurrentTrainerAsync(owner);
        var trainingId = await CreateTrainingAsync(owner, "A training whose owner has a name");

        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        var page = await ReadTrainingsAsync(administrator, string.Empty);

        var row = page.Items.Should().ContainSingle().Subject;

        row.Id.Should().Be(trainingId);
        row.TrainerId.Should().Be(profile.Id);
        row.TrainerName.Should().Be($"{profile.Firstname} {profile.Lastname}");
    }

    /// <summary>
    /// A filter, is applied before the page rather than after it.
    /// </summary>
    /// <remarks>
    /// The fact that only a real database can settle, and the one a paged list hides best. With the
    /// filter composed after the page, the first page of a filtered query would hold whatever
    /// survived filtering the first page of everything, and the total would count the whole table —
    /// numbers that still look like numbers. Six trainings, two withheld, a page size of two: a
    /// count of six or a page holding an unwithheld training both fail this.
    /// </remarks>
    [Fact]
    public async Task AFilter_IsAppliedBeforeThePageRatherThanAfterIt()
    {
        var owner = await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);
        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        var withheld = new List<Guid>();

        for (var index = 1; index <= 6; index++)
        {
            var trainingId = await CreateTrainingAsync(owner, $"Catalogue training {index:D2}");

            // The two withheld ones are the oldest, so that a filter applied to a page rather than
            // to the query would page right past them.
            if (index <= 2)
            {
                (await administrator.PostAsJsonAsync(
                    $"/Administration/trainings/{trainingId}/withhold",
                    new WithholdTrainingHttpRequest { Reason = "Taken down for this proof." }))
                    .EnsureSuccessStatusCode();

                withheld.Add(trainingId);
            }
        }

        var page = await ReadTrainingsAsync(administrator, "?status=Withheld&page=1&pageSize=2");

        page.TotalCount.Should().Be(2, "the count answers the filtered question, not the table");
        page.TotalPages.Should().Be(1);
        page.HasNextPage.Should().BeFalse();
        page.Items.Select(item => item.Id).Should().BeEquivalentTo(withheld);
    }

    /// <summary>
    /// A suspended trainer, is findable by standing and by name.
    /// </summary>
    [Fact]
    public async Task ASuspendedTrainer_IsFindableByStandingAndByName()
    {
        var trainer = await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);
        var profile = await CurrentTrainerAsync(trainer);

        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        (await administrator.PostAsJsonAsync(
            $"/Administration/trainers/{profile.Id}/suspend",
            new SuspendTrainerHttpRequest { Reason = "Repeated breaches of the content policy." }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var byStanding = await ReadTrainersAsync(administrator, "?status=Suspended");

        var row = byStanding.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(profile.Id);
        row.ContactEmail.Should().Be(profile.ContactEmail);
        row.SuspensionReason.Should().Be("Repeated breaches of the content policy.");

        // The same trainer, found the other way: the term is matched against the name, and the two
        // criteria compose rather than replace one another.
        var byName = await ReadTrainersAsync(
            administrator, $"?status=Suspended&search={Uri.EscapeDataString(profile.Lastname)}");

        byName.Items.Should().ContainSingle().Which.Id.Should().Be(profile.Id);

        // And by the contact address, which is the third column the term reaches. Asserted because
        // it is the one an administrator actually has to hand when a report arrives by email.
        var byAddress = await ReadTrainersAsync(
            administrator, $"?search={Uri.EscapeDataString(profile.ContactEmail)}");

        byAddress.Items.Should().ContainSingle().Which.Id.Should().Be(profile.Id);
    }

    /// <summary>
    /// A term nothing answers to, empties the page without emptying the envelope.
    /// </summary>
    [Fact]
    public async Task ATermNothingAnswersTo_EmptiesThePageWithoutEmptyingTheEnvelope()
    {
        await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);
        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        var page = await ReadTrainersAsync(administrator, "?search=nobodyiscalledthis");

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(1, "an empty result is page 1 of 1, which is what a pager renders");
        page.HasNextPage.Should().BeFalse();
        page.HasPreviousPage.Should().BeFalse();
    }

    /// <summary>
    /// A status no domain type declares, is refused by naming the parameter.
    /// </summary>
    /// <remarks>
    /// Refused at model binding rather than answered with an empty page, because a caller who
    /// mistyped a filter and got nothing back would read it as "nothing matches". The parameter is
    /// named so they can tell the two apart.
    /// </remarks>
    [Fact]
    public async Task AStatusNoDomainTypeDeclares_IsRefusedByNamingTheParameter()
    {
        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        var refused = await administrator.GetAsync("/Administration/trainers?status=Banished");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement
            .GetProperty("errors")
            .TryGetProperty(nameof(AdministrationTrainerFilterHttpRequest.Status), out _)
            .Should().BeTrue("the caller is told which parameter is at fault");
    }

    /// <summary>
    /// A page beyond the published maximum, is refused rather than served.
    /// </summary>
    /// <remarks>
    /// The listing inherits the cap ADR 0029 published, because it binds the same
    /// <see cref="PaginationHttpRequest"/> as every other paged read rather than declaring page
    /// coordinates of its own. A listing with its own bounds is how the cap becomes a suggestion.
    /// </remarks>
    [Fact]
    public async Task APageBeyondThePublishedMaximum_IsRefusedRatherThanServed()
    {
        var administrator = await AuthHelper.SignInAsAdministratorAsync(Factory);

        (await administrator.GetAsync("/Administration/trainings?pageSize=1000"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A trainer, is refused both listings.
    /// </summary>
    /// <remarks>
    /// The half that matters most, because it is the read this API deliberately withdrew.
    /// <c>/Trainer/all</c> served every trainer's name and contact address to any authenticated
    /// caller; what makes the replacement a different read is exactly this refusal (ADR 0055).
    /// </remarks>
    [Fact]
    public async Task ATrainer_IsRefusedBothListings()
    {
        var trainer = await AuthHelper.RegisterAndGetAuthenticatedClientAsync(Factory);

        (await trainer.GetAsync("/Administration/trainers")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await trainer.GetAsync("/Administration/trainings")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// An anonymous caller, is refused both listings with unauthorized.
    /// </summary>
    [Fact]
    public async Task AnAnonymousCaller_IsRefusedBothListingsWithUnauthorized()
    {
        var client = Factory.CreateClient();

        (await client.GetAsync("/Administration/trainers")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await client.GetAsync("/Administration/trainings")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<Guid> CreateTrainingAsync(HttpClient client, string title)
    {
        var created = await client.PostAsJsonAsync("/Training", TrainingRequests.Valid(title));
        created.EnsureSuccessStatusCode();

        return await created.Content.ReadFromJsonAsync<Guid>();
    }

    private static async Task<TrainerHttpResponse> CurrentTrainerAsync(HttpClient client)
    {
        var profile = await client.GetAsync("/Trainer/me");
        profile.EnsureSuccessStatusCode();

        return (await profile.Content.ReadFromJsonAsync<TrainerHttpResponse>())!;
    }

    private static async Task<PagedHttpResponse<AdministrationTrainerHttpResponse>> ReadTrainersAsync(
        HttpClient client, string query)
    {
        var response = await client.GetAsync($"/Administration/trainers{query}");
        response.EnsureSuccessStatusCode();

        return (await response.Content
            .ReadFromJsonAsync<PagedHttpResponse<AdministrationTrainerHttpResponse>>())!;
    }

    private static async Task<PagedHttpResponse<AdministrationTrainingHttpResponse>> ReadTrainingsAsync(
        HttpClient client, string query)
    {
        var response = await client.GetAsync($"/Administration/trainings{query}");
        response.EnsureSuccessStatusCode();

        return (await response.Content
            .ReadFromJsonAsync<PagedHttpResponse<AdministrationTrainingHttpResponse>>())!;
    }
}
