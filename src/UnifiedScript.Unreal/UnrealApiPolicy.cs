using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Unreal;

public enum UnrealApiPolicyAction
{
    Allow,
    Deny,
}

public enum UnrealBindingRoute
{
    Auto,
    DirectThunk,
    CallFrame,
    DataPlaneOperation,
    ReflectionFallback,
}

public sealed record UnrealApiPolicyRule(
    string MemberPath,
    UnrealApiPolicyAction Action,
    UnrealBindingRoute Route = UnrealBindingRoute.Auto,
    string? DiagnosticCode = null,
    string? Reason = null);

/// <summary>
/// Explicit policy applied after the API Contract selects a project surface. Exact rules are
/// deliberately deterministic: broad wildcard policy belongs in a future contract version.
/// </summary>
public sealed record UnrealApiPolicy(
    UnrealApiPolicyAction DefaultAction,
    IReadOnlyList<UnrealApiPolicyRule> Rules)
{
    public static UnrealApiPolicy AllowSelected { get; } = new(UnrealApiPolicyAction.Allow, []);
}

public sealed record UnrealBindingCoverageBaseline(
    int MinimumMemberCount,
    int MinimumDirectThunkCount,
    int MinimumCallFrameCount,
    int MinimumDataPlaneOperationCount,
    int MinimumReflectionFallbackCount,
    int MaximumRejectedCount);

public sealed record UnrealApiPolicyDecision(
    string MemberPath,
    UnrealApiPolicyAction Action,
    UnrealBindingRoute Route,
    string? DiagnosticCode,
    string? Reason);

public sealed record UnrealApiPolicyEvaluation(
    UnrealApiPolicy Policy,
    IReadOnlyDictionary<string, UnrealApiPolicyDecision> Decisions)
{
    public UnrealApiPolicyDecision For(string memberPath) =>
        Decisions.TryGetValue(memberPath, out UnrealApiPolicyDecision? decision)
            ? decision
            : throw new InvalidOperationException($"No API policy decision exists for '{memberPath}'.");
}

public static class UnrealApiPolicyEvaluator
{
    public static UnrealApiPolicy Normalize(UnrealApiPolicy? policy)
    {
        policy ??= UnrealApiPolicy.AllowSelected;
        if (!Enum.IsDefined(policy.DefaultAction) || policy.Rules is null)
        {
            throw new JsonException("The Unreal API policy has invalid required fields.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        UnrealApiPolicyRule[] rules = policy.Rules.Select(rule =>
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.MemberPath) ||
                !Enum.IsDefined(rule.Action) || !Enum.IsDefined(rule.Route) ||
                !paths.Add(rule.MemberPath) ||
                rule.Action == UnrealApiPolicyAction.Deny &&
                (rule.Route != UnrealBindingRoute.Auto ||
                 !IsDiagnosticCode(rule.DiagnosticCode) || string.IsNullOrWhiteSpace(rule.Reason)) ||
                rule.Action == UnrealApiPolicyAction.Allow &&
                (rule.DiagnosticCode is not null || rule.Reason is not null))
            {
                throw new JsonException("The Unreal API policy contains an invalid or duplicate rule.");
            }
            return rule with
            {
                MemberPath = rule.MemberPath.Trim(),
                DiagnosticCode = rule.DiagnosticCode?.Trim(),
                Reason = rule.Reason?.Trim(),
            };
        }).OrderBy(static rule => rule.MemberPath, StringComparer.Ordinal).ToArray();
        return new UnrealApiPolicy(policy.DefaultAction, rules);
    }

    public static UnrealApiPolicyEvaluation Evaluate(
        UnrealReflectionManifest selectedReflection,
        UnrealApiPolicy? policy)
    {
        UnrealReflectionManifest normalized = UnrealReflectionJson.NormalizeAndValidate(selectedReflection);
        UnrealApiPolicy normalizedPolicy = Normalize(policy);
        IReadOnlyDictionary<string, UnrealApiPolicyRule> rules = normalizedPolicy.Rules
            .ToDictionary(static rule => rule.MemberPath, StringComparer.Ordinal);
        var decisions = new Dictionary<string, UnrealApiPolicyDecision>(StringComparer.Ordinal);
        foreach (string path in EnumerateMemberPaths(normalized))
        {
            UnrealApiPolicyRule? rule = rules.GetValueOrDefault(path);
            UnrealApiPolicyAction action = rule?.Action ?? normalizedPolicy.DefaultAction;
            UnrealBindingRoute route = rule?.Route ?? UnrealBindingRoute.Auto;
            decisions.Add(path, new UnrealApiPolicyDecision(
                path,
                action,
                route,
                rule?.DiagnosticCode ?? (action == UnrealApiPolicyAction.Deny ? "USUE5001" : null),
                rule?.Reason ?? (action == UnrealApiPolicyAction.Deny
                    ? "The selected API member is denied by the project API policy."
                    : null)));
        }
        string? staleRule = rules.Keys.FirstOrDefault(path => !decisions.ContainsKey(path));
        if (staleRule is not null)
        {
            throw new JsonException($"Unreal API policy rule '{staleRule}' is not selected by the API Contract.");
        }
        return new UnrealApiPolicyEvaluation(normalizedPolicy, decisions);
    }

    public static UnrealReflectionManifest Filter(
        UnrealReflectionManifest selectedReflection,
        UnrealApiPolicyEvaluation evaluation,
        bool native)
    {
        UnrealReflectedType[] types = selectedReflection.Types.Select(type => type with
        {
            Properties = type.Properties.Where(property => Include(evaluation.For(property.Path), native)).ToArray(),
            Functions = type.Functions.Where(function => Include(evaluation.For(function.Path), native)).ToArray(),
            Delegates = type.Delegates.Where(item => Include(evaluation.For(item.Path), native: false)).ToArray(),
        }).ToArray();
        return UnrealReflectionJson.NormalizeAndValidate(selectedReflection with { Types = types });
    }

    public static IEnumerable<string> EnumerateMemberPaths(UnrealReflectionManifest manifest) =>
        manifest.Types.SelectMany(static type =>
            type.Properties.Select(static item => item.Path)
                .Concat(type.Functions.Select(static item => item.Path))
                .Concat(type.Delegates.Select(static item => item.Path)));

    private static bool Include(UnrealApiPolicyDecision decision, bool native) =>
        decision.Action == UnrealApiPolicyAction.Allow &&
        decision.Route != UnrealBindingRoute.DataPlaneOperation &&
        (!native || decision.Route != UnrealBindingRoute.ReflectionFallback);

    private static bool IsDiagnosticCode(string? value) =>
        value is { Length: >= 8 } && value.StartsWith("USUE", StringComparison.Ordinal) &&
        value[4..].All(char.IsDigit);
}
