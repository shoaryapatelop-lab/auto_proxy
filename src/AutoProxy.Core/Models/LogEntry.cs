namespace AutoProxy.Core.Models;

public enum LogLevel
{
    Verbose,
    Info,
    Warning,
    Error,
}

public class LogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public long? NetworkId { get; set; }
    public long? ProxyId { get; set; }
}