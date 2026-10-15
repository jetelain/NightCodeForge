namespace NightCodeForge.Core.Domain;

/// <summary>
/// Persistent key-value system setting or feature configuration.
/// </summary>
public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
