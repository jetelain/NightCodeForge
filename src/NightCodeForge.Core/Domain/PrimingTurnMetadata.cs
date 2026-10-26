namespace NightCodeForge.Core.Domain;

/// <summary>
/// Metadata payload serialized into <see cref="ConversationTurn.MetadataJson"/> for synthetic priming turns.
/// </summary>
public sealed record PrimingTurnMetadata(
    string CallId,
    bool IsPrimed,
    string SourceStageId,
    string PrimingType);
