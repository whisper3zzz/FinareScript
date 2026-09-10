using System.Text.Json;
using Finare.Core;

namespace Finare.Semantics;

public sealed record MethodEffectSubject(
    string AssemblyName,
    string ContainingType,
    string MethodName);

public sealed record MethodEffectRule(
    string Id,
    string Assembly,
    string ContainingType,
    string Method,
    MethodEffects Effects,
    int Priority = 0);

public sealed record MethodEffectCatalogDocument(
    string SchemaVersion,
    IReadOnlyList<MethodEffectRule> Rules)
{
    public const string CurrentSchemaVersion = "unifiedscript.effects/v0.1";
}

public sealed class MethodEffectCatalog
{
    private static readonly IReadOnlyList<MethodEffectRule> DefaultRules =
    [
        new("system.math", "*", "System.Math", "*", MethodEffects.None),
        new("system.mathf", "*", "System.MathF", "*", MethodEffects.None),
    ];

    private readonly IReadOnlyList<MethodEffectRule> rules;

    public MethodEffectCatalog(IEnumerable<MethodEffectRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        MethodEffectRule[] materialized = rules.ToArray();
        foreach (MethodEffectRule rule in materialized)
        {
            ValidateRule(rule);
        }

        string? duplicateId = materialized
            .GroupBy(static rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateId is not null)
        {
            throw new ArgumentException($"Duplicate effect rule ID '{duplicateId}'.", nameof(rules));
        }

        this.rules = materialized
            .OrderByDescending(static rule => rule.Priority)
            .ThenByDescending(GetSpecificity)
            .ThenBy(static rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        Id = SemanticId.FromKey(string.Join(
            '\n',
            this.rules.Select(static rule =>
                $"{rule.Id}|{rule.Priority}|{rule.Assembly}|{rule.ContainingType}|{rule.Method}|{rule.Effects}")));
    }

    public IReadOnlyList<MethodEffectRule> Rules => rules;

    public SemanticId Id { get; }

    public static MethodEffectCatalog CreateDefault() => new(DefaultRules);

    public MethodEffectCatalog WithOverrides(IEnumerable<MethodEffectRule> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        MethodEffectRule[] promoted = overrides
            .Select(static rule => rule with { Priority = Math.Max(rule.Priority, 100) })
            .ToArray();
        HashSet<string> overriddenIds = promoted
            .Select(static rule => rule.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new MethodEffectCatalog(rules
            .Where(rule => !overriddenIds.Contains(rule.Id))
            .Concat(promoted));
    }

    public MethodEffects Resolve(MethodEffectSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        MethodEffectRule? match = rules.FirstOrDefault(rule => Matches(rule, subject));
        return match?.Effects ?? MethodEffects.ReadsState | MethodEffects.WritesState;
    }

    public static MethodEffectCatalogDocument ReadDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        MethodEffectCatalogDocument? document = JsonSerializer.Deserialize<MethodEffectCatalogDocument>(
            json,
            UnifiedJson.CreateOptions());
        if (document is null)
        {
            throw new JsonException("The effect catalog document was empty.");
        }

        if (document.SchemaVersion != MethodEffectCatalogDocument.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported effect catalog schema '{document.SchemaVersion}'. " +
                $"Expected '{MethodEffectCatalogDocument.CurrentSchemaVersion}'.");
        }

        _ = new MethodEffectCatalog(document.Rules);
        return document;
    }

    private static bool Matches(MethodEffectRule rule, MethodEffectSubject subject) =>
        MatchesPart(rule.Assembly, subject.AssemblyName) &&
        MatchesPart(rule.ContainingType, subject.ContainingType) &&
        MatchesPart(rule.Method, subject.MethodName);

    private static bool MatchesPart(string pattern, string value) =>
        pattern == "*" || string.Equals(pattern, value, StringComparison.Ordinal);

    private static int GetSpecificity(MethodEffectRule rule) =>
        (rule.Assembly == "*" ? 0 : 1) +
        (rule.ContainingType == "*" ? 0 : 1) +
        (rule.Method == "*" ? 0 : 1);

    private static void ValidateRule(MethodEffectRule rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.ContainingType);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Method);
    }
}
