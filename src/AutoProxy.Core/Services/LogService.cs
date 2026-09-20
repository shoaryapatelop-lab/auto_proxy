using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;

namespace AutoProxy.Core.Services;

public class LogService : ILogService, IDisposable
{
    private const int RingBufferCapacity = 1000;
    private const int MaxFileSizeBytes = 2 * 1024 * 1024;

    private readonly ILogRepository? _repository;
    private readonly Channel<LogEntry> _channel = Channel.CreateUnbounded<LogEntry>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly string _logDirectory;

    public LogService(ILogRepository? repository = null, string? logDirectory = null)
    {
        _repository = repository;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoProxy", "logs");
        _writer = Task.Run(WriteLoopAsync);
    }

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> GetRecentlyAdded(int limit) =>
        _ring.Reverse().Take(limit).ToList();

    public void Verbose(string category, string message, string? detail = null) =>
        Write(LogLevel.Verbose, category, message, detail);

    public void Info(string category, string message, string? detail = null,
        long? networkId = null, long? proxyId = null) =>
        Write(LogLevel.Info, category, message, detail, networkId, proxyId);

    public void Warning(string category, string message, string? detail = null,
        long? networkId = null, long? proxyId = null) =>
        Write(LogLevel.Warning, category, message, detail, networkId, proxyId);

    public void Error(string category, string message, string? detail = null,
        long? networkId = null, long? proxyId = null) =>
        Write(LogLevel.Error, category, message, detail, networkId, proxyId);

    private void Write(LogLevel level, string category, string message, string? detail,
        long? networkId = null, long? proxyId = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = Sanitize(category),
            Message = Sanitize(message),
            Detail = detail is null ? null : CleanCredentials(detail),
            NetworkId = networkId,
            ProxyId = proxyId,
        };

        entry.Message = CleanCredentials(entry.Message);

        if (_ring.Count >= RingBufferCapacity && _ring.TryDequeue(out _)) { }
        _ring.Enqueue(entry);
        _channel.Writer.TryWrite(entry);
        EntryAdded?.Invoke(this, entry);
    }

    private async Task WriteLoopAsync()
    {
        // DB logs are written in batches; file logs are appended per entry.
        var drain = new List<LogEntry>();
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var entry)) drain.Add(entry);

                AppendToFile(drain);
                if (_repository is not null)
                {
                    try
                    {
                        await _repository.AddManyAsync(drain, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        AppendToFile(new[]
                        {
                            new LogEntry
                            {
                                Timestamp = DateTime.Now,
                                Level = LogLevel.Warning,
                                Category = "Log",
                                Message = "Failed to persist log entries to database.",
                                Detail = ex.Message,
                            },
                        });
                    }
                }

                drain.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AppendToFile(IReadOnlyList<LogEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var today = DateTime.Now.ToString("yyyyMMdd");
            var path = Path.Combine(_logDirectory, $"autoproxy-{today}.log");

            if (File.Exists(path) && new FileInfo(path).Length > MaxFileSizeBytes)
                File.Move(path, path + ".old", overwrite: true);

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                sb.Append('[')
                  .Append(entry.Timestamp.ToString("HH:mm:ss"))
                  .Append("] [")
                  .Append(entry.Level.ToString().ToUpperInvariant())
                  .Append("] [")
                  .Append(entry.Category)
                  .Append("] ")
                  .Append(entry.Message);
                if (entry.Detail is not null)
                    sb.Append(" — ").Append(entry.Detail);
                sb.AppendLine();
            }

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never take the application down.
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CredentialInUri =
        new(
            @"[A-Za-z0-9][A-Za-z0-9\.\-_%]*:[^@\s/]+@",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CleanCredentials(string value)
    {
        // Defensive: strip anything that looks like user:pass@host, including
        // credentials embedded in URLs (scheme://user:pass@host).
        return CredentialInUri.Replace(value, "[credentials hidden]@");
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        return value.Length > 4000 ? value[..4000] : value;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writer.Wait(TimeSpan.FromSeconds(3));
        _cts.Dispose();
    }
}