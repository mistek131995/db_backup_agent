using System.Text.Json.Serialization;

namespace DbBackupAgent.Enums;

public enum RestoreTaskStatus
{
    Pending,

    [JsonStringEnumMemberName("in_progress")]
    InProgress,

    Success,
    Failed,
    Partial,
}
