using System.Text;

namespace Finare.Unreal;

/// <summary>
/// Defines the author-facing C# name for reflected Unreal members while preserving the
/// reflection name and member path as the runtime identity. Unreal's K2_ prefix is an
/// implementation detail for Blueprint-callable C++ wrappers and is omitted from the managed
/// surface when doing so remains deterministic.
/// </summary>
public static class UnrealManagedNaming
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while",
    };

    public static string FunctionName(UnrealReflectedFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return FunctionName(function.Name);
    }

    public static string FunctionName(string reflectedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reflectedName);
        string authorName = reflectedName.StartsWith("K2_", StringComparison.Ordinal) &&
            reflectedName.Length > 3
                ? reflectedName[3..]
                : reflectedName;
        return ToIdentifier(authorName);
    }

    private static string ToIdentifier(string value)
    {
        var identifier = new StringBuilder(value.Length + 1);
        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            identifier.Append('_');
        }
        foreach (char character in value)
        {
            identifier.Append(char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '_');
        }
        string result = identifier.ToString();
        return Keywords.Contains(result) ? $"@{result}" : result;
    }
}
