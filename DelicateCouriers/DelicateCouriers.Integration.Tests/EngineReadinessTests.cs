using System.Net;
using DelicateCouriers.Integration.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// Telling the Engine's refusals apart.
///
/// This matters more than it looks. Onboarding a merchant here does not create the two things
/// the Engine needs — an account mapped to their reference, and that account granted to this
/// service's credential — and both are super_admin-only, so this service can only report them.
/// If it reports them wrongly, an operator follows the wrong instruction and the merchant still
/// cannot sell.
///
/// The classification reads the Engine's message text, because the Engine answers all of these
/// with the same `forbidden` code. These tests are the tripwire for that wording changing: they
/// carry the Engine's exact strings, so a drift breaks a test here rather than a merchant there.
/// </summary>
public class EngineReadinessTests
{
    private static EngineClient ClientReturning(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("http://engine.test/"),
        };
        return new EngineClient(http, NullLogger<EngineClient>.Instance);
    }

    [Fact]
    public async Task A_merchant_with_no_reference_is_reported_before_any_call()
    {
        // No reference means nothing to ask about; asking anyway would report whatever the
        // Engine says about an empty header, which is a different and more confusing problem.
        var client = ClientReturning(HttpStatusCode.OK, "null");

        var status = await client.CheckAccountAsync("   ", CancellationToken.None);

        Assert.Equal(EngineReadiness.NoAccountMapping, status.Readiness);
        Assert.False(status.CanBook);
    }

    [Fact]
    public async Task Nothing_booked_yet_is_the_success_case()
    {
        // The probe reference is one no real booking uses, so 404 is the expected answer and
        // means the call was authorised.
        var client = ClientReturning(HttpStatusCode.NotFound, "");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.Ready, status.Readiness);
        Assert.True(status.CanBook);
    }

    [Fact]
    public async Task An_unmapped_reference_is_distinguished_from_a_missing_grant()
    {
        var client = ClientReturning(HttpStatusCode.Forbidden,
            """{"statusCode":403,"code":"forbidden","message":"no account is mapped to this reference"}""");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.NoAccountMapping, status.Readiness);
        Assert.False(status.CanBook);
    }

    [Fact]
    public async Task A_missing_grant_is_its_own_state()
    {
        var client = ClientReturning(HttpStatusCode.Forbidden,
            """{"statusCode":403,"code":"forbidden","message":"this credential may not act for that account"}""");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.NotGrantedToCredential, status.Readiness);
    }

    [Fact]
    public async Task A_suspended_account_is_not_confused_with_a_configuration_gap()
    {
        var client = ClientReturning(HttpStatusCode.Forbidden,
            """{"statusCode":403,"code":"forbidden","message":"account is not active"}""");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.AccountNotActive, status.Readiness);
    }

    [Fact]
    public async Task A_rejected_credential_is_reported_as_ours_not_the_merchants()
    {
        // 401 is about this service's own credential. Reporting it as a merchant problem would
        // send an operator to fix the wrong thing, on every merchant in turn.
        var client = ClientReturning(HttpStatusCode.Unauthorized,
            """{"statusCode":401,"code":"unauthorized","message":"invalid credential"}""");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.CredentialRejected, status.Readiness);
    }

    [Fact]
    public async Task An_unrecognised_refusal_admits_it_rather_than_guessing()
    {
        // If the Engine's wording changes, saying "unknown" and quoting it is honest. Guessing
        // the nearest match would send someone to run the wrong fix with confidence.
        var client = ClientReturning(HttpStatusCode.Forbidden,
            """{"statusCode":403,"code":"forbidden","message":"some new refusal nobody has seen"}""");

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.Unknown, status.Readiness);
        Assert.Contains("some new refusal", status.Detail);
    }

    [Fact]
    public async Task An_unreachable_Engine_is_not_reported_as_a_merchant_problem()
    {
        var http = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://engine.test/"),
        };
        var client = new EngineClient(http, NullLogger<EngineClient>.Instance);

        var status = await client.CheckAccountAsync("store-7", CancellationToken.None);

        Assert.Equal(EngineReadiness.Unknown, status.Readiness);
        Assert.False(status.CanBook);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
