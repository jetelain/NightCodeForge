using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Domain;

/// <summary>
/// Structured diagnostic execution log entry for an issue or stage.
/// </summary>
public class ExecutionLog
{
    public int Id { get; set; }
    public int IssueId { get; set; }
    public string? StageId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public LogLevel Level { get; set; } = LogLevel.Information;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }

    public Issue? Issue { get; set; }
}
