using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Network;

public class ConnectivityTester : IConnectivityTester
{
    private static readonly string[] PortalKeywords =
    {
        "sign in", "login", "log in", "captive", "portal", "connect to", "wifi login",
        "welcome to", "usage", "auth",
    };

    private const int MaxAttempts = 2;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _client;
    private readonly ILogService _log;

    public ConnectivityTester(HttpClient client, ILogService log)
    {
        _client = client;
        _log = log;
    }

    public async Task<DirectTestResult> TestDirectAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var urls = settings.EffectiveProbeUrls();
        if (urls.Count == 0)
        {
            _log.Warning("Verify", "No connectivity test endpoints are configured.",
                "Add at least one endpoint in Settings.");
            return new DirectTestResult { Endpoints = Array.Empty<EndpointResult>() };
        }

        var tasks = urls.Select(u => ProbeAsync(u, settings, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        return new DirectTestResult { Endpoints = results };
    }

    private async Task<EndpointResult> ProbeAsync(
        string url,
        AppSettings settings,
        CancellationToken outerToken)
    {
        EndpointResult? last = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            last = await ProbeOnceAsync(url, settings, outerToken);

            if (last.Success || attempt == MaxAttempts || !IsTransientFailure(last))
                break;

            try
            {
                await Task.Delay(TransientRetryDelay, outerToken);
            }
            catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
            {
                break;
            }
        }

        return last!;
    }

    private async Task<EndpointResult> ProbeOnceAsync(
        string url,
        AppSettings settings,
        CancellationToken outerToken)
    {
        var endpoint = new EndpointResult { Url = url };
        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(settings.DirectTestTimeoutMs);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "AutoProxy/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            stopwatch.Stop();
            endpoint.HttpStatus = (int)response.StatusCode;
            endpoint.LatencyMs = stopwatch.ElapsedMilliseconds;

            endpoint.RedirectUrl = DetectAuthorityRedirect(response, url);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var previewBytes = await ReadPreviewBytesAsync(stream, 4096, cts.Token);
            var body = ReadPreview(previewBytes);

            // HTTP 511 is the standardized captive-portal status code.
            bool successStatus = response.IsSuccessStatusCode ||
                                 response.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                                     or System.Net.HttpStatusCode.Found
                                     or System.Net.HttpStatusCode.RedirectKeepVerb
                                     or System.Net.HttpStatusCode.TemporaryRedirect;

            if ((int)response.StatusCode == 511 || LooksLikeLoginPage(body))
            {
                endpoint.LooksLikeCaptivePortal = true;
                endpoint.Success = false;
                endpoint.Classification = endpoint.RedirectUrl is not null
                    ? EndpointClassification.RedirectedToLogin
                    : EndpointClassification.LoginPageContent;
                endpoint.Error = "Redirected to a login/captive portal page";
                _log.Info("Verify", $"Endpoint redirected or served a login page: {url}",
                    $"Final: {endpoint.RedirectUrl ?? "same host"} Body: {Truncate(body, 80)}");
                return endpoint;
            }

            if (!successStatus)
            {
                endpoint.Success = false;
                endpoint.Classification = EndpointClassification.HttpError;
                endpoint.Error = $"HTTP {(int)response.StatusCode}";
                return endpoint;
            }

            if (endpoint.RedirectUrl is not null)
            {
                // Redirected away but not to a recognizable login page —
                // the network is intercepting responses it shouldn't.
                endpoint.Success = false;
                endpoint.Classification = EndpointClassification.ContentMismatch;
                endpoint.Error = $"Redirected to {endpoint.RedirectUrl}";
                return endpoint;
            }

            endpoint.Success = true;
            endpoint.Classification = EndpointClassification.Healthy;
            return endpoint;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            endpoint.Success = false;
            endpoint.LatencyMs = stopwatch.ElapsedMilliseconds;
            endpoint.Classification = outerToken.IsCancellationRequested
                ? EndpointClassification.Cancelled
                : EndpointClassification.Timeout;
            endpoint.Error = outerToken.IsCancellationRequested ? "Cancelled" : "Timeout";
            return endpoint;
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            endpoint.Success = false;
            endpoint.LatencyMs = stopwatch.ElapsedMilliseconds;
            endpoint.Classification = ClassifyEndpoint(ex);
            endpoint.Error = ConnectivityTester.Classify(ex);
            return endpoint;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            endpoint.Success = false;
            endpoint.LatencyMs = stopwatch.ElapsedMilliseconds;
            endpoint.Classification = EndpointClassification.ConnectionFailed;
            endpoint.Error = $"{ex.GetType().Name}: {ex.Message}";
            return endpoint;
        }
    }

    private static string? DetectAuthorityRedirect(HttpResponseMessage response, string originalUrl)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null &&
            !string.Equals(
                finalUri.GetLeftPart(UriPartial.Authority),
                new Uri(originalUrl).GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return finalUri.ToString();
        }

        return null;
    }

    private bool LooksLikeLoginPage(string body) =>
        body.Contains("<html", StringComparison.OrdinalIgnoreCase) &&
        PortalKeywords.Any(k => body.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientFailure(EndpointResult endpoint) =>
        endpoint.Classification is EndpointClassification.Timeout
            or EndpointClassification.ConnectionFailed
            or EndpointClassification.DnsFailure
        || (endpoint.Classification == EndpointClassification.HttpError &&
            (endpoint.HttpStatus >= 500 || endpoint.HttpStatus == 429));

    private static EndpointClassification ClassifyEndpoint(HttpRequestException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException sock)
            {
                return sock.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        EndpointClassification.DnsFailure,
                    SocketError.ConnectionRefused or SocketError.ConnectionReset or
                    SocketError.ConnectionAborted or SocketError.NetworkUnreachable or
                    SocketError.HostUnreachable =>
                        EndpointClassification.ConnectionFailed,
                    _ => EndpointClassification.ConnectionFailed,
                };
            }

            if (inner is AuthenticationException)
                return EndpointClassification.TlsError;
        }

        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return EndpointClassification.DnsFailure;
        if (ex.HttpRequestError == HttpRequestError.ConnectionError)
            return EndpointClassification.ConnectionFailed;
        if (ex.HttpRequestError is HttpRequestError.SecureConnectionError or
            HttpRequestError.InvalidResponse)
            return EndpointClassification.TlsError;

        return EndpointClassification.ConnectionFailed;
    }

    private static async Task<byte[]> ReadPreviewBytesAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        // Never buffer an unbounded response body: a misconfigured or hostile
        // endpoint must not be able to exhaust memory. We only need a snippet
        // to detect captive-portal markers.
        byte[] buffer = new byte[maxBytes];
        int total = 0;

        while (total < maxBytes)
        {
            int chunkSize = Math.Min(81920, maxBytes - total);
            int read = await stream.ReadAsync(buffer, total, chunkSize, cancellationToken);
            if (read <= 0) break;
            total += read;
        }

        if (total == buffer.Length) return buffer;

        var result = new byte[total];
        Buffer.BlockCopy(buffer, 0, result, 0, total);
        return result;
    }

    private static string ReadPreview(byte[] bytes)
    {
        const int max = 2048;
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, max));
        return text.ToLowerInvariant();
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var s = value.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    internal static string Classify(HttpRequestException ex)
    {
        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException sock)
            {
                return sock.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        "DNS failure",
                    SocketError.ConnectionRefused or SocketError.ConnectionReset or
                    SocketError.ConnectionAborted or SocketError.NetworkUnreachable or
                    SocketError.HostUnreachable =>
                        "Connection failed",
                    _ => $"Network error ({sock.SocketErrorCode})",
                };
            }

            if (inner is System.Security.Authentication.AuthenticationException)
                return "TLS error";
        }

        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return "DNS failure";
        if (ex.HttpRequestError == HttpRequestError.ConnectionError)
            return "Connection failed";
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError ||
            ex.HttpRequestError == HttpRequestError.InvalidResponse)
            return "TLS error";

        return string.IsNullOrWhiteSpace(ex.Message) ? "Request failed" : ex.Message;
    }
}