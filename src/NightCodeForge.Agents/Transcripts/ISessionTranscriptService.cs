namespace NightCodeForge.Agents.Transcripts;

/// <summary>Persists markdown execution transcripts for agent sessions.</summary>
public interface ISessionTranscriptService
{
    Task<string> SaveTranscriptAsync(
        TranscriptMetadata metadata,
        IReadOnlyList<TranscriptEntry> entries,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}

