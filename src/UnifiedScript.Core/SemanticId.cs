using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UnifiedScript.Core;

/// <summary>
/// Stable identity shared by text syntax, HIR, visual nodes, and future bytecode.
/// </summary>
public readonly partial record struct SemanticId
{
    private const string Prefix = "us_";

    public SemanticId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!SemanticIdPattern().IsMatch(value))
        {
            throw new ArgumentException($"Invalid semantic ID '{value}'.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public static SemanticId FromKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string payload = Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
        return new SemanticId(Prefix + payload);
    }

    public static SemanticId New()
    {
        byte[] random = RandomNumberGenerator.GetBytes(16);
        return new SemanticId(Prefix + Convert.ToHexString(random).ToLowerInvariant());
    }

    public static bool TryParse(string? value, out SemanticId id)
    {
        if (value is not null && SemanticIdPattern().IsMatch(value))
        {
            id = new SemanticId(value);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^us_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticIdPattern();
}
