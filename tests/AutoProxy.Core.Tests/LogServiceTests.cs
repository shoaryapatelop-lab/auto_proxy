using AutoProxy.Core.Services;
using Xunit;

namespace AutoProxy.Core.Tests;

public class LogServiceTests : IDisposable
{
    private readonly string _logDirectory;
    private readonly LogService _log;

    public LogServiceTests()
    {
        _logDirectory = Path.Combine(
            Path.GetTempPath(), "autoproxy-tests", Guid.NewGuid().ToString("N"));
        _log = new LogService(logDirectory: _logDirectory);
    }

    [Theory]
    [InlineData(
        "connecting to https://user:secret@example.com/proxy.pac",
        "connecting to https://[credentials hidden]@example.com/proxy.pac")]
    [InlineData(
        "user:secret@proxy.example:8080",
        "[credentials hidden]@proxy.example:8080")]
    [InlineData(
        "joined user:secret@socks.example and user:p2@http.example",
        "joined [credentials hidden]@socks.example and [credentials hidden]@http.example")]
    [InlineData("No credentials anywhere in this message.", "No credentials anywhere in this message.")]
    public void Credentials_in_log_messages_are_redacted(string input, string expected)
    {
        _log.Info("Test", input);

        var entry = _log.GetRecentlyAdded(1).First();

        Assert.Equal(expected, entry.Message);
    }

    [Fact]
    public void Credentials_in_detail_are_redacted()
    {
        _log.Warning("Test", "detail check", "handler auth user:hunter2@10.0.0.5:3128");

        var entry = _log.GetRecentlyAdded(1).First();

        Assert.Equal("handler auth [credentials hidden]@10.0.0.5:3128", entry.Detail);
    }

    [Fact]
    public void Credentials_after_scheme_delimiter_are_redacted()
    {
        _log.Error("Test", "", "http://alice:sup3rS3cret@http.example:3128");

        var entry = _log.GetRecentlyAdded(1).First();

        Assert.Equal("http://[credentials hidden]@http.example:3128", entry.Detail);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}