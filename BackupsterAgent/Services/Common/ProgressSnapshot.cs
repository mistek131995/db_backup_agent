namespace BackupsterAgent.Services.Common;

public sealed record ProgressSnapshot<TStage>(
    TStage Stage,
    long? Processed,
    long? Total,
    string? Unit,
    string? CurrentItem)
    where TStage : struct, Enum;
