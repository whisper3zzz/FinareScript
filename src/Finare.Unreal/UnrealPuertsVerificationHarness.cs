using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finare.Unreal;

public sealed record UnrealPuertsVerificationIssue(
    string CaseId,
    string Kind,
    string? Observable,
    string? PuertsValue,
    [property: JsonPropertyName("unifiedScriptValue")]
    string? FinareValue,
    string Message);

public sealed record UnrealPuertsVerificationSummary(
    int RequiredCaseCount,
    int PassedCaseCount,
    int BlockedCaseCount,
    int MissingCaseCount,
    int PendingCaseCount,
    int MismatchedCaseCount,
    int StaleCaseCount,
    int IssueCount);

public sealed record UnrealPuertsVerificationReport(
    string SchemaVersion,
    string ProjectName,
    string ScanFingerprint,
    string PlanId,
    bool MatchedMetadata,
    bool Passed,
    UnrealPuertsVerificationSummary Summary,
    IReadOnlyList<UnrealPuertsVerificationIssue> Issues,
    IReadOnlyList<string> Diagnostics);

public static class UnrealPuertsVerificationReportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(UnrealPuertsVerificationReport report) =>
        JsonSerializer.Serialize(report, Options).ReplaceLineEndings("\n") + "\n";
}

public static class UnrealPuertsVerificationHarnessRunner
{
    public static UnrealPuertsVerificationReport Compare(
        UnrealPuertsVerificationHarness harness,
        UnrealPuertsVerificationEvidence puerts,
        UnrealPuertsVerificationEvidence unifiedScript)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(puerts);
        ArgumentNullException.ThrowIfNull(unifiedScript);
        ValidateHarness(harness);
        ValidateEvidence(puerts);
        ValidateEvidence(unifiedScript);

        var diagnostics = new List<string>();
        ValidateMetadata(harness, puerts, "puerts", diagnostics);
        ValidateMetadata(harness, unifiedScript, "unifiedScript", diagnostics);
        var issues = new List<UnrealPuertsVerificationIssue>();
        Dictionary<string, UnrealPuertsVerificationCaseEvidence> puertsCases = IndexEvidence(
            puerts,
            issues);
        Dictionary<string, UnrealPuertsVerificationCaseEvidence> unifiedCases = IndexEvidence(
            unifiedScript,
            issues);
        if (harness.PlanBlocked)
        {
            issues.Add(new UnrealPuertsVerificationIssue(
                "PLAN",
                "blocked-plan",
                null,
                null,
                null,
                "The migration plan is blocked and cannot produce passing verification."));
        }
        var harnessIds = new HashSet<string>(
            harness.Cases.Select(static item => item.Id),
            StringComparer.Ordinal);
        foreach (string stale in puertsCases.Keys
                     .Concat(unifiedCases.Keys)
                     .Where(id => !harnessIds.Contains(id))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            issues.Add(new UnrealPuertsVerificationIssue(
                stale,
                "stale-case",
                null,
                puertsCases.ContainsKey(stale) ? "present" : null,
                unifiedCases.ContainsKey(stale) ? "present" : null,
                "Evidence contains a case that is absent from the verification harness."));
        }

        int passedCases = 0;
        foreach (UnrealPuertsVerificationCase testCase in harness.Cases)
        {
            int issuesBefore = issues.Count;
            if (testCase.Status == "blocked")
            {
                issues.Add(new UnrealPuertsVerificationIssue(
                    testCase.Id,
                    "blocked-case",
                    null,
                    null,
                    null,
                    $"Verification case '{testCase.SourceKey}' is blocked by its migration plan."));
                continue;
            }
            bool hasPuerts = puertsCases.TryGetValue(
                testCase.Id,
                out UnrealPuertsVerificationCaseEvidence? puertsCase);
            bool hasUnified = unifiedCases.TryGetValue(
                testCase.Id,
                out UnrealPuertsVerificationCaseEvidence? unifiedCase);
            if (!hasPuerts || !hasUnified)
            {
                issues.Add(new UnrealPuertsVerificationIssue(
                    testCase.Id,
                    "missing-case",
                    null,
                    hasPuerts ? "present" : null,
                    hasUnified ? "present" : null,
                    "Both runtime lanes must publish evidence for every required case."));
                continue;
            }
            UnrealPuertsVerificationCaseEvidence resolvedPuerts = puertsCase!;
            UnrealPuertsVerificationCaseEvidence resolvedUnified = unifiedCase!;
            if (resolvedPuerts.Status != "passed" || resolvedUnified.Status != "passed")
            {
                issues.Add(new UnrealPuertsVerificationIssue(
                    testCase.Id,
                    "pending-case",
                    null,
                    resolvedPuerts.Status,
                    resolvedUnified.Status,
                    "Both runtime lanes must mark the case as passed before comparison."));
            }
            foreach (string observable in testCase.RequiredObservables)
            {
                bool hasPuertsValue = resolvedPuerts.Observables.TryGetValue(
                    observable,
                    out string? puertsValue);
                bool hasUnifiedValue = resolvedUnified.Observables.TryGetValue(
                    observable,
                    out string? unifiedValue);
                if (!hasPuertsValue || !hasUnifiedValue ||
                    string.IsNullOrWhiteSpace(puertsValue) ||
                    string.IsNullOrWhiteSpace(unifiedValue) ||
                    puertsValue == "TODO" || unifiedValue == "TODO")
                {
                    issues.Add(new UnrealPuertsVerificationIssue(
                        testCase.Id,
                        "missing-observable",
                        observable,
                        hasPuertsValue ? puertsValue : null,
                        hasUnifiedValue ? unifiedValue : null,
                        "Both runtime lanes must publish a non-placeholder observable value."));
                }
                else if (!string.Equals(puertsValue, unifiedValue, StringComparison.Ordinal))
                {
                    issues.Add(new UnrealPuertsVerificationIssue(
                        testCase.Id,
                        "observable-mismatch",
                        observable,
                        puertsValue,
                        unifiedValue,
                        "Puerts and Finare observable values differ."));
                }
            }
            bool hadExistingCaseIssue = issues.Take(issuesBefore)
                .Any(issue => issue.CaseId == testCase.Id);
            if (!hadExistingCaseIssue && issues.Count == issuesBefore)
            {
                passedCases++;
            }
        }

        UnrealPuertsVerificationIssue[] orderedIssues = issues
            .OrderBy(static issue => issue.CaseId, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Kind, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Observable, StringComparer.Ordinal)
            .ToArray();
        int staleCases = orderedIssues
            .Where(static issue => issue.Kind == "stale-case")
            .Select(static issue => issue.CaseId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int mismatchedCases = orderedIssues
            .Where(static issue => issue.Kind == "observable-mismatch")
            .Select(static issue => issue.CaseId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int missingCases = orderedIssues
            .Where(static issue => issue.Kind == "missing-case")
            .Select(static issue => issue.CaseId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int pendingCases = orderedIssues
            .Where(static issue => issue.Kind is "pending-case" or "missing-observable")
            .Select(static issue => issue.CaseId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        int blockedCases = harness.Cases.Count(static item => item.Status == "blocked");
        bool metadataMatches = diagnostics.Count == 0;
        return new UnrealPuertsVerificationReport(
            "unifiedscript.puerts-verification-report/v0.1",
            harness.ProjectName,
            harness.ScanFingerprint,
            harness.PlanId,
            metadataMatches,
            metadataMatches && orderedIssues.Length == 0 &&
                passedCases == harness.Cases.Count,
            new UnrealPuertsVerificationSummary(
                harness.Cases.Count,
                passedCases,
                blockedCases,
                missingCases,
                pendingCases,
                mismatchedCases,
                staleCases,
                orderedIssues.Length),
            orderedIssues,
            diagnostics.Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, UnrealPuertsVerificationCaseEvidence> IndexEvidence(
        UnrealPuertsVerificationEvidence evidence,
        ICollection<UnrealPuertsVerificationIssue> issues)
    {
        var result = new Dictionary<string, UnrealPuertsVerificationCaseEvidence>(
            StringComparer.Ordinal);
        foreach (UnrealPuertsVerificationCaseEvidence item in evidence.Cases)
        {
            if (!result.TryAdd(item.CaseId, item))
            {
                issues.Add(new UnrealPuertsVerificationIssue(
                    item.CaseId,
                    "duplicate-case",
                    null,
                    evidence.Runtime == "puerts" ? "duplicate" : null,
                    evidence.Runtime == "unifiedScript" ? "duplicate" : null,
                    $"Runtime lane '{evidence.Runtime}' published duplicate case evidence."));
            }
        }
        return result;
    }

    private static void ValidateMetadata(
        UnrealPuertsVerificationHarness harness,
        UnrealPuertsVerificationEvidence evidence,
        string expectedRuntime,
        ICollection<string> diagnostics)
    {
        if (evidence.ProjectName != harness.ProjectName)
        {
            diagnostics.Add(
                $"{expectedRuntime} projectName '{evidence.ProjectName}' does not match " +
                $"'{harness.ProjectName}'.");
        }
        if (evidence.ScanFingerprint != harness.ScanFingerprint)
        {
            diagnostics.Add($"{expectedRuntime} scanFingerprint does not match the harness.");
        }
        if (evidence.PlanId != harness.PlanId)
        {
            diagnostics.Add($"{expectedRuntime} planId does not match the harness.");
        }
        if (evidence.Runtime != expectedRuntime)
        {
            diagnostics.Add(
                $"Expected runtime lane '{expectedRuntime}', received '{evidence.Runtime}'.");
        }
    }

    private static void ValidateHarness(UnrealPuertsVerificationHarness harness)
    {
        if (harness.SchemaVersion != "unifiedscript.puerts-verification-harness/v0.1" ||
            string.IsNullOrWhiteSpace(harness.ProjectName) ||
            string.IsNullOrWhiteSpace(harness.ScanFingerprint) ||
            string.IsNullOrWhiteSpace(harness.PlanId) ||
            harness.OwnershipPolicy != "exactly-one-runtime-owns-each-world-lifecycle" ||
            harness.RuntimeLanes is null ||
            !harness.RuntimeLanes.SequenceEqual(["puerts", "unifiedScript"]) ||
            harness.Cases is null ||
            harness.Cases.Any(static item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                item.Status is not ("required" or "blocked") ||
                item.RequiredObservables is null || item.RequiredObservables.Count == 0) ||
            harness.Cases.Select(static item => item.Id)
                .Distinct(StringComparer.Ordinal).Count() != harness.Cases.Count)
        {
            throw new ArgumentException("Puerts verification harness is incomplete or invalid.");
        }
    }

    private static void ValidateEvidence(UnrealPuertsVerificationEvidence evidence)
    {
        if (evidence.SchemaVersion != "unifiedscript.puerts-verification-evidence/v0.1" ||
            string.IsNullOrWhiteSpace(evidence.ProjectName) ||
            string.IsNullOrWhiteSpace(evidence.ScanFingerprint) ||
            string.IsNullOrWhiteSpace(evidence.PlanId) ||
            evidence.Runtime is not ("puerts" or "unifiedScript") ||
            evidence.Cases is null ||
            evidence.Cases.Any(static item =>
                string.IsNullOrWhiteSpace(item.CaseId) ||
                item.Status is not ("pending" or "passed" or "failed") ||
                item.Observables is null))
        {
            throw new ArgumentException("Puerts verification evidence is incomplete or invalid.");
        }
    }
}
