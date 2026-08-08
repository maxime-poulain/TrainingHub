using AwesomeAssertions;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TrainingHub.Blazor.Client.Components;
using Xunit;

namespace TrainingHub.Blazor.Client.Tests.Components;

/// <summary>
/// Behaviour covered for the redirect an unauthenticated visitor lands on.
/// </summary>
/// <remarks>
/// Three things live here and nowhere else. The address the visitor asked for is carried into
/// <c>returnUrl</c> so a deep link survives signing in — without it every bookmark and every
/// expired session lands on the catalogue. The address is made relative before it is carried,
/// which is what stops a sign-in page from becoming a phishing hop: a redirect target taken from
/// the address bar and replayed after authentication is an open redirect the moment it is allowed
/// to name a host.
/// <para>
/// And the third, which the administration pages are the first to need: a caller who <em>is</em>
/// signed in and is refused anyway must not be sent to a sign-in page. That was harmless while
/// every guarded page asked only for a session, because the only way to be refused was to be
/// nobody. Against a page that asks for an authority it is a loop — sign in, come back, be refused,
/// be sent to sign in.
/// </para>
/// </remarks>
public sealed class RedirectToLoginTests : ComponentTest
{
    /// <summary>
    /// Initialised, a deep link, remembers where the visitor was going.
    /// </summary>
    [Fact]
    public void Initialised_ADeepLink_RemembersWhereTheVisitorWasGoing()
    {
        // Arrange
        var navigation = Navigation("http://localhost/trainings/create");

        // Act
        Render<RedirectToLogin>();

        // Assert
        navigation.Uri.Should().Be("http://localhost/login?returnUrl=%2Ftrainings%2Fcreate");
    }

    /// <summary>
    /// Initialised, at the root, has nothing to remember.
    /// </summary>
    [Fact]
    public void Initialised_AtTheRoot_HasNothingToRemember()
    {
        // Arrange
        var navigation = Navigation("http://localhost/");

        // Act
        Render<RedirectToLogin>();

        // Assert
        navigation.Uri.Should().Be("http://localhost/login");
    }

    /// <summary>
    /// Initialised, the address carries a query, escapes it into one parameter.
    /// </summary>
    /// <remarks>
    /// Unescaped, the requested query string would merge into the sign-in page's own and the part
    /// after the first ampersand would be read as a second parameter of the login address.
    /// </remarks>
    [Fact]
    public void Initialised_TheAddressCarriesAQuery_EscapesItIntoOneParameter()
    {
        // Arrange
        var navigation = Navigation("http://localhost/trainings?page=2&size=10");

        // Act
        Render<RedirectToLogin>();

        // Assert
        navigation.Uri.Should().Be("http://localhost/login?returnUrl=%2Ftrainings%3Fpage%3D2%26size%3D10");
    }

    /// <summary>
    /// Initialised, a signed-in caller without the authority, says so instead of asking them to
    /// sign in again.
    /// </summary>
    /// <remarks>
    /// Asserted on the address rather than on the words: what makes this a defect is the
    /// navigation, and a test that only read the copy would keep passing if the redirect came
    /// back underneath it.
    /// </remarks>
    [Fact]
    public void Initialised_ASignedInCallerWithoutTheAuthority_SaysSoInsteadOfAskingThemToSignInAgain()
    {
        // Arrange — authorization first: resolving a service seals the container, and Navigation
        // resolves the navigation manager.
        AddAuthorization().SetAuthorized("trainer@example.com");
        var navigation = Navigation("http://localhost/administration/trainers");

        // Act
        var page = Render<RedirectToLogin>();

        // Assert
        navigation.Uri.Should().Be(
            "http://localhost/administration/trainers",
            "a caller who is already signed in has nothing to gain from the sign-in page");

        page.Markup.Should().Contain("authority", "the refusal has to say what is actually missing");
    }

    private BunitNavigationManager Navigation(string uri)
    {
        var navigation = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(uri);

        return navigation;
    }
}
