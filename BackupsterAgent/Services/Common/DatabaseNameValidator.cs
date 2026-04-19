namespace BackupsterAgent.Services.Common;

public static class DatabaseNameValidator
{
    public const int MaxLength = 128;

    public static bool IsValid(string? name, out string? reason)
    {
        if (string.IsNullOrEmpty(name))
        {
            reason = "имя пустое";
            return false;
        }

        if (name.Length > MaxLength)
        {
            reason = $"длина {name.Length} символов больше лимита {MaxLength}";
            return false;
        }

        if (name.Contains(".."))
        {
            reason = "имя содержит подряд две точки";
            return false;
        }

        foreach (var ch in name)
        {
            if (!IsAllowedChar(ch))
            {
                reason = $"имя содержит недопустимый символ '{ch}' (U+{(int)ch:X4})";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool IsAllowedChar(char ch) =>
        (ch >= 'a' && ch <= 'z') ||
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= '0' && ch <= '9') ||
        ch == '_' || ch == '-' || ch == '.';
}
