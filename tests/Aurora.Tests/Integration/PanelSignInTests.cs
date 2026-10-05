using System.Net;
using System.Net.Http.Json;
using Aurora.Adapters.Consent;
using Aurora.Adapters.Time;
using Aurora.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Aurora.Tests.Integration;

/// <summary>
/// Opening the control panel with the operator passphrase, for an Aurora that runs as a service and
/// has no console of its own to print a link on.
/// </summary>
/// <remarks>
/// Each test has its own instance: enrolment and the lockout are state on disk that the next test
/// must not inherit.
/// </remarks>
public sealed class PanelSignInTests
{
    private const string Secret = "operator-only-secret";

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static void Enrol(AuroraAppFactory server) =>
        new Pbkdf2PassphraseAuthenticator(server.PassphrasePath, new SystemClock(), new PassphraseOptions(Iterations: 1_000))
            .Enroll(Secret);

    private static HttpClient Browser(AuroraAppFactory server) =>
        server.CreateDefaultClient(new CookieContainerHandler());

    private static Task<HttpResponseMessage> SignInAsync(HttpClient browser, string passphrase) =>
        browser.PostAsJsonAsync("/ui/session/passphrase", new { passphrase }, Ct());

    [Fact]
    public async Task WithNoPassphraseEnrolledThereIsNothingToSignInWith()
    {
        await using var server = new AuroraAppFactory();
        using HttpClient browser = Browser(server);

        // The console link is the way in until a passphrase exists.
        Assert.Equal(HttpStatusCode.NotFound, (await SignInAsync(browser, Secret)).StatusCode);
    }

    [Fact]
    public async Task ThePassphraseOpensAnOperatorSessionAndNothingElseDoes()
    {
        await using var server = new AuroraAppFactory();
        Enrol(server);
        using HttpClient browser = Browser(server);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(browser, "a guess")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/v1/approvals", Ct())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(browser, Secret)).StatusCode);

        // An operator session, the kind only a person holds: it reads what is waiting to be decided.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/v1/approvals", Ct())).StatusCode);
    }

    [Fact]
    public async Task TheLockoutCoversSigningInAsItCoversApproving()
    {
        await using var server = new AuroraAppFactory();
        Enrol(server);
        using HttpClient browser = Browser(server);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SignInAsync(browser, $"guess {attempt}");
        }

        // Locked: the right passphrase is refused too, so guessing gains nothing by being patient
        // inside the window.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync(browser, Secret)).StatusCode);
    }

    [Fact]
    public async Task ABrowserWithoutASessionIsSentToSignIn()
    {
        await using var server = new AuroraAppFactory();
        using HttpClient browser = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ui/");
        request.Headers.Accept.ParseAdd("text/html");

        HttpResponseMessage response = await browser.SendAsync(request, Ct());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/ui/login", response.Headers.Location?.OriginalString);

        HttpResponseMessage page = await browser.GetAsync("/ui/login", Ct());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("operator passphrase", await page.Content.ReadAsStringAsync(Ct()), StringComparison.Ordinal);
    }
}
