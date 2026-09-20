using System.Net;
using System.Net.Sockets;
using AutoProxy.Core.Models;
using AutoProxy.Core.Network;
using Xunit;

namespace AutoProxy.Core.Tests;

public class ConnectivityTesterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? Responder { get; set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                Responder?.Invoke(request, cancellationToken) ?? new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var tcs = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        }
    }

    private const string Probe = "https://cp.cloudflare.com/generate_204";

    private static AppSettings Settings(int timeoutMs = 8000) =>
        new() { ProbeUrls = new[] { Probe }, DirectTestTimeoutMs = timeoutMs };

    private static ConnectivityTester Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new SilentLog());

    [Fact]
    public async Task Healthy_endpoint_reports_success()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.True(result.InternetAvailable);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.True(endpoint.Success);
        Assert.Equal(EndpointClassification.Healthy, endpoint.Classification);
        Assert.Equal(204, endpoint.HttpStatus);
    }

    [Fact]
    public async Task Login_page_content_is_classified_as_login_page()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Please sign in to use this WiFi</body></html>"),
            },
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.False(result.InternetAvailable);
        Assert.True(result.CaptivePortalHint);
        Assert.Equal(DirectFailureReason.CaptivePortal, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.LoginPageContent, endpoint.Classification);
        Assert.False(endpoint.Success);
    }

    [Fact]
    public async Task Redirect_to_login_host_is_classified_as_login_redirect()
    {
        var handler = new StubHandler
        {
            Responder = (request, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://login.example.com/portal"),
                Content = new StringContent("<html>please log in to continue</html>"),
            },
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.False(result.InternetAvailable);
        Assert.True(result.CaptivePortalHint);
        Assert.Equal(DirectFailureReason.CaptivePortal, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.RedirectedToLogin, endpoint.Classification);
        Assert.NotNull(endpoint.RedirectUrl);
    }

    [Fact]
    public async Task Http_511_is_treated_as_captive_portal()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage((HttpStatusCode)511),
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.False(result.InternetAvailable);
        Assert.True(result.CaptivePortalHint);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.LoginPageContent, endpoint.Classification);
    }

    [Fact]
    public async Task Redirect_without_login_markers_is_content_mismatch()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.other.net/some/file"),
                Content = new StringContent("<html><body>ok</body></html>"),
            },
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.False(result.InternetAvailable);
        Assert.False(result.CaptivePortalHint);
        Assert.Equal(DirectFailureReason.ContentBlocked, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.ContentMismatch, endpoint.Classification);
    }

    [Fact]
    public async Task Five_hundred_status_is_retried_and_recovers()
    {
        var calls = 0;
        var handler = new StubHandler
        {
            Responder = (_, _) =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            },
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(result.InternetAvailable);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.Healthy, endpoint.Classification);
    }

    [Fact]
    public async Task Dns_failure_is_classified()
    {
        var handler = new StubHandler
        {
            Responder = (_, _) => throw new HttpRequestException(
                "name resolved failed",
                new SocketException((int)SocketError.HostNotFound)),
        };

        var result = await Create(handler).TestDirectAsync(Settings(), CancellationToken.None);

        Assert.False(result.InternetAvailable);
        Assert.True(result.AnyDnsFailure);
        Assert.Equal(DirectFailureReason.DnsFailure, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.DnsFailure, endpoint.Classification);
    }

    [Fact]
    public async Task Timeout_is_classified_after_the_timeout_window()
    {
        var handler = new HangingHandler();
        var settings = Settings(timeoutMs: 30);

        var result = await Create(handler).TestDirectAsync(settings, CancellationToken.None);

        Assert.True(result.AnyTimeout);
        Assert.Equal(DirectFailureReason.Timeout, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.Timeout, endpoint.Classification);
        Assert.False(endpoint.Success);
    }

    [Fact]
    public async Task External_cancellation_is_classified_as_cancelled()
    {
        var handler = new HangingHandler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Create(handler).TestDirectAsync(Settings(), cts.Token);

        Assert.Equal(DirectFailureReason.TestCancelled, result.FailureReason);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(EndpointClassification.Cancelled, endpoint.Classification);
    }

    [Fact]
    public async Task No_endpoints_returns_empty_result()
    {
        var handler = new StubHandler();
        var settings = new AppSettings { ProbeUrls = Array.Empty<string>() };

        var result = await Create(handler).TestDirectAsync(settings, CancellationToken.None);

        Assert.Empty(result.Endpoints);
        Assert.False(result.InternetAvailable);
    }
}

public class DirectTestResultClassificationTests
{
    private static DirectTestResult Subject(params EndpointResult[] endpoints) =>
        new() { Endpoints = endpoints };

    private static EndpointResult Endpoint(EndpointClassification c, bool success = false) =>
        new() { Url = "https://x/", Success = success, Classification = c };

    [Fact]
    public void Healthy_endpoint_yields_none_reason()
    {
        var result = Subject(Endpoint(EndpointClassification.Healthy, success: true));
        Assert.Equal(DirectFailureReason.None, result.FailureReason);
        Assert.Contains("verified", result.DiagnosticSummary);
    }

    [Fact]
    public void Login_redirect_yields_captive_portal_reason()
    {
        var result = Subject(new EndpointResult
        {
            Url = "https://x/",
            Success = false,
            Classification = EndpointClassification.RedirectedToLogin,
            RedirectUrl = "http://portal/login",
        });
        Assert.Equal(DirectFailureReason.CaptivePortal, result.FailureReason);
        Assert.Contains("authentication", result.DiagnosticSummary);
    }

    [Fact]
    public void Content_mismatch_yields_blocked_reason()
    {
        var result = Subject(Endpoint(EndpointClassification.ContentMismatch));
        Assert.Equal(DirectFailureReason.ContentBlocked, result.FailureReason);
        Assert.Contains("blocked", result.DiagnosticSummary);
    }

    [Fact]
    public void Dns_failure_beats_timeout_in_priority()
    {
        var result = Subject(
            Endpoint(EndpointClassification.Timeout),
            Endpoint(EndpointClassification.DnsFailure));
        Assert.Equal(DirectFailureReason.DnsFailure, result.FailureReason);
    }

    [Fact]
    public void Unclassified_failures_fall_back_to_offline()
    {
        var result = Subject(new EndpointResult { Url = "https://x/", Success = false });
        Assert.Equal(DirectFailureReason.Offline, result.FailureReason);
        Assert.Contains("no direct connectivity", result.DiagnosticSummary);
    }

    [Fact]
    public void All_cancelled_yields_test_cancelled()
    {
        var result = Subject(
            Endpoint(EndpointClassification.Cancelled),
            Endpoint(EndpointClassification.Cancelled));
        Assert.Equal(DirectFailureReason.TestCancelled, result.FailureReason);
    }
}