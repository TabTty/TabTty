namespace TabTty.Core;

public enum LogCollisionPolicy { Error, Overwrite, Append }

internal static class LogCollisionPolicies
{
    internal static IReadOnlyList<LogCollisionPolicy> All { get; } = Array.AsReadOnly(Enum.GetValues<LogCollisionPolicy>());

    internal static string Format(LogCollisionPolicy policy) => policy switch
    {
        LogCollisionPolicy.Error => "error",
        LogCollisionPolicy.Overwrite => "overwrite",
        LogCollisionPolicy.Append => "append",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    internal static bool TryParse(string? text, out LogCollisionPolicy policy)
    {
        foreach (var choice in All)
        {
            if (text == Format(choice))
            {
                policy = choice;
                return true;
            }
        }
        policy = default;
        return false;
    }
}
