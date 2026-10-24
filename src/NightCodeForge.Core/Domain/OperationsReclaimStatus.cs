namespace NightCodeForge.Core.Domain;

/// <summary>
/// Status of host GPU VRAM eviction during global operations pause.
/// </summary>
public enum OperationsReclaimStatus
{
    Idle = 0,
    Evicting = 1,
    Evicted = 2,
    Failed = 3
}
