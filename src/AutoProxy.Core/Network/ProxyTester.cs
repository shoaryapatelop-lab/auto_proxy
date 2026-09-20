using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Network;

public class ProxyTester : IProxyTester
{
    private readonly ILogService _log;
    private readonly ICredentialManager _credentials;

    public ProxyTester(ILogService log, ICredentialManager credentials)
    {
        _log = log;
        _credentials = credentials;
    }

    public Task<ProxyTestResult> TestAsync(
        ProxyProfile proxy,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        return RunTestAsync(proxy, settings, cancellationToken);
    }

    public async Task<(bool IsServing, long LatencyMs)> GetProxyHealthAsync(
        ProxyProfile proxy,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var urls = settings.EffectiveProbeUrls();
        var stopwatch = Stopwatch.StartNew();
        if (urls.Count == 0)
        {
            _log.Warning("ProxyTest",
                "No probe endpoints are configured — cannot verify proxy health.");
            return (false, 0);
        }

        using var handler = BuildHandler(proxy, settings.IgnoreCertificateErrors);
        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(settings.ProxyTestTimeoutMs);

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoProxy/1.0");
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    return (true, stopwatch.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Try the next configured endpoint.
            }
        }

        stopwatch.Stop();
        return (false, stopwatch.ElapsedMilliseconds);
    }

    private async Task<ProxyTestResult> RunTestAsync(
        ProxyProfile proxy,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var urls = settings.EffectiveProbeUrls();
        if (urls.Count == 0)
        {
            return ProxyTestResult.Failure(proxy, ProxyTestOutcome.ProtocolError,
                "No probe endpoints configured.");
        }

        using var handler = BuildHandler(proxy, settings.IgnoreCertificateErrors);
        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(settings.ProxyTestTimeoutMs);
        var stopwatch = Stopwatch.StartNew();
        ProxyTestResult? lastFailure = null;

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "AutoProxy/1.0");
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    var result = ProxyTestResult.Failure(proxy, ProxyTestOutcome.AuthenticationFailed,
                        "The proxy rejected the supplied credentials (HTTP 407).");
                    result.AuthChallenge = true;
                    lastFailure = result;
                    _log.Warning("ProxyTest",
                        $"Proxy rejected supplied credentials: {proxy.Name} (HTTP 407).",
                        "Verify username/password in the proxy settings.");
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    stopwatch.Stop();
                    return new ProxyTestResult
                    {
                        Proxy = proxy,
                        Outcome = ProxyTestOutcome.Success,
                        HttpStatus = (int)response.StatusCode,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                    };
                }

                lastFailure = ProxyTestResult.Failure(proxy, ProxyTestOutcome.ProtocolError,
                    $"HTTP {(int)response.StatusCode}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new ProxyTestResult
                {
                    Proxy = proxy,
                    Outcome = ProxyTestOutcome.Timeout,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Error = $"Timeout after {settings.ProxyTestTimeoutMs} ms",
                };
            }
            catch (HttpRequestException ex)
            {
                lastFailure = ProxyTestResult.Failure(proxy, Classify(ex),
                    ConnectivityTester.Classify(ex));
                if (IsDnsFailure(ex)) break;
            }
            catch (Exception ex)
            {
                lastFailure = ProxyTestResult.Failure(proxy, ProxyTestOutcome.ProtocolError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        stopwatch.Stop();
        var final = lastFailure ??
                    ProxyTestResult.Failure(proxy, ProxyTestOutcome.ProxyUnreachable);
        final.LatencyMs = stopwatch.ElapsedMilliseconds;
        return final;
    }

    private SocketsHttpHandler BuildHandler(ProxyProfile proxy, bool ignoreCertificateErrors)
    {
        NetworkCredential? credentials = null;

        if (proxy.AuthenticationRequired && !string.IsNullOrEmpty(proxy.CredentialTarget))
        {
            var creds = _credentials.ReadCredential(proxy.CredentialTarget);
            if (creds is not null)
            {
                credentials = new NetworkCredential(creds.Value.Username, creds.Value.Password);
            }
        }

        var webProxy = new WebProxy(
            $"http://{ProxyValidator.FormatAddress(proxy.Host, proxy.Port)}");
        if (credentials is not null)
        {
            webProxy.Credentials = credentials;
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = webProxy,
            AllowAutoRedirect = true,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
        };

        if (ignoreCertificateErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    private static bool IsDnsFailure(HttpRequestException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException sock &&
                sock.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
                return true;
        }
        return ex.HttpRequestError == HttpRequestError.NameResolutionError;
    }

    private static ProxyTestOutcome Classify(HttpRequestException ex) =>
        IsDnsFailure(ex)
            ? ProxyTestOutcome.DnsFailure
            : ex.InnerException is AuthenticationException
                ? ProxyTestOutcome.TlsError
                : ProxyTestOutcome.ConnectFailure;
}