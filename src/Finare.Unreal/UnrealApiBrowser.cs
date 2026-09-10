using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finare.Core;

namespace Finare.Unreal;

public sealed record UnrealApiBrowserSources(
    string ContractSchemaVersion,
    string NodeCatalogSchemaVersion,
    string CoverageSchemaVersion,
    string InputSha256);

public sealed record UnrealApiBrowserSummary(
    int TypeCount,
    int MemberCount,
    int NodeCount,
    int DirectThunkCount,
    int CallFrameCount,
    int DataPlaneOperationCount,
    int ReflectionFallbackCount,
    int RejectedCount,
    int DiagnosticCount);

public sealed record UnrealApiBrowserConstraint(
    UnrealApiThreadRequirement ThreadRequirement,
    UnrealApiWorldRequirement WorldRequirement,
    string? WorldContextParameter,
    bool CallableWithoutWorldContext,
    bool Latent,
    string? LatentInfoParameter,
    bool Deprecated,
    string? DeprecationMessage);

public sealed record UnrealApiBrowserMember(
    string Id,
    string MemberPath,
    string MemberId,
    string Kind,
    string Title,
    string Category,
    string Tooltip,
    bool Pure,
    UnrealBindingDisposition? Disposition,
    bool Available,
    UnrealApiBrowserConstraint Constraints,
    IReadOnlyList<UnrealApiNodePin> Pins,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record UnrealApiBrowserType(
    string Path,
    string Name,
    string ManagedTypeName,
    string Category,
    IReadOnlyList<UnrealApiBrowserMember> Members);

public sealed record UnrealApiBrowserDiagnostic(
    string Code,
    UnifiedDiagnosticSeverity Severity,
    string Message,
    IReadOnlyList<string> MemberPaths);

/// <summary>
/// Human-facing, deterministic projection of the same API Contract, visual-node catalog,
/// and coverage report consumed by generated bindings and editor node palettes.
/// </summary>
public sealed record UnrealApiBrowserDocument(
    string SchemaVersion,
    string ContractName,
    string ManagedNamespace,
    UnrealEngineIdentity Engine,
    UnrealApiBrowserSources Sources,
    UnrealApiBrowserSummary Summary,
    IReadOnlyList<UnrealApiBrowserType> Types,
    IReadOnlyList<UnrealApiBrowserDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "unifiedscript.unreal-api-browser/v0.1";
}

public static class UnrealApiBrowserGenerator
{
    public static UnrealApiBrowserDocument Generate(
        UnrealApiContractProjection projection,
        UnrealApiNodeCatalog nodeCatalog,
        UnrealBindingCoverageReport coverage)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(nodeCatalog);
        ArgumentNullException.ThrowIfNull(coverage);
        if (nodeCatalog.SchemaVersion != UnrealApiNodeCatalog.CurrentSchemaVersion ||
            coverage.SchemaVersion != UnrealBindingCoverageReport.CurrentSchemaVersion ||
            nodeCatalog.ContractName != projection.Contract.ContractName ||
            nodeCatalog.ManagedNamespace != projection.Contract.ManagedNamespace ||
            nodeCatalog.Engine != coverage.Engine ||
            nodeCatalog.Engine != projection.Reflection.Engine)
        {
            throw new ArgumentException(
                "API browser inputs must come from one matching Unreal API Contract projection.");
        }

        IReadOnlyDictionary<string, UnrealBindingCoverageEntry> coverageByPath = coverage.Entries
            .ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        UnrealApiBrowserType[] types = nodeCatalog.Types
            .OrderBy(static type => type.Path, StringComparer.Ordinal)
            .Select(type => new UnrealApiBrowserType(
                type.Path,
                type.Name,
                type.ManagedTypeName,
                type.Category,
                type.Nodes
                    .OrderBy(static node => node.Category, StringComparer.Ordinal)
                    .ThenBy(static node => node.Title, StringComparer.Ordinal)
                    .ThenBy(static node => node.Id, StringComparer.Ordinal)
                    .Select(node => CreateMember(node, coverageByPath.GetValueOrDefault(node.MemberPath)))
                    .ToArray()))
            .ToArray();

        UnrealApiBrowserDiagnostic[] diagnostics = CreateDiagnostics(coverage);
        string contractJson = UnrealApiContractJson.Serialize(
            projection.Contract,
            writeIndented: false);
        string nodeJson = UnrealApiNodeCatalogGenerator.Serialize(
            nodeCatalog,
            writeIndented: false);
        string coverageJson = UnrealBindingCoverageJson.Serialize(
            coverage,
            writeIndented: false);
        string fingerprintInput = string.Concat(contractJson, nodeJson, coverageJson);
        string inputSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)))
            .ToLowerInvariant();

        return new UnrealApiBrowserDocument(
            UnrealApiBrowserDocument.CurrentSchemaVersion,
            projection.Contract.ContractName,
            projection.Contract.ManagedNamespace,
            nodeCatalog.Engine,
            new UnrealApiBrowserSources(
                projection.Contract.SchemaVersion,
                nodeCatalog.SchemaVersion,
                coverage.SchemaVersion,
                inputSha256),
            new UnrealApiBrowserSummary(
                coverage.Summary.TypeCount,
                coverage.Summary.MemberCount,
                types.Sum(static type => type.Members.Count),
                coverage.Summary.DirectThunkCount,
                coverage.Summary.CallFrameCount,
                coverage.Summary.DataPlaneOperationCount,
                coverage.Summary.ReflectionFallbackCount,
                coverage.Summary.RejectedCount,
                diagnostics.Length),
            types,
            diagnostics);
    }

    private static UnrealApiBrowserMember CreateMember(
        UnrealApiNodeDescriptor node,
        UnrealBindingCoverageEntry? coverage)
    {
        UnrealBindingDisposition? disposition = coverage?.Disposition;
        return new UnrealApiBrowserMember(
            node.Id,
            node.MemberPath,
            node.MemberId,
            node.Kind,
            node.Title,
            node.Category,
            node.Tooltip,
            node.Pure,
            disposition,
            disposition != UnrealBindingDisposition.Rejected,
            new UnrealApiBrowserConstraint(
                node.ThreadRequirement,
                node.WorldRequirement,
                node.WorldContextParameter,
                node.CallableWithoutWorldContext,
                node.Latent,
                node.LatentInfoParameter,
                node.Deprecated,
                node.DeprecationMessage),
            node.Pins,
            coverage?.DiagnosticCodes ?? []);
    }

    private static UnrealApiBrowserDiagnostic[] CreateDiagnostics(
        UnrealBindingCoverageReport coverage)
    {
        var occurrences = new List<(string Code, string Message, string Path, bool Error)>();
        foreach (UnrealBindingCoverageEntry entry in coverage.Entries)
        {
            foreach (string code in entry.DiagnosticCodes)
            {
                occurrences.Add((
                    code,
                    string.IsNullOrWhiteSpace(entry.Message)
                        ? "The generated binding route reported this diagnostic."
                        : entry.Message,
                    entry.Path,
                    entry.Disposition == UnrealBindingDisposition.Rejected));
            }
        }
        foreach (string gateDiagnostic in coverage.Gate.Diagnostics)
        {
            int separator = gateDiagnostic.IndexOf(':');
            string code = separator > 0 ? gateDiagnostic[..separator] : "USUE5100";
            occurrences.Add((code, gateDiagnostic, "$contract", true));
        }

        return occurrences
            .GroupBy(static item => item.Code, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new UnrealApiBrowserDiagnostic(
                group.Key,
                group.Any(static item => item.Error)
                    ? UnifiedDiagnosticSeverity.Error
                    : UnifiedDiagnosticSeverity.Warning,
                string.Join(
                    " ",
                    group.Select(static item => item.Message)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)),
                group.Select(static item => item.Path)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }
}

public static class UnrealApiBrowserJson
{
    public static string Serialize(
        UnrealApiBrowserDocument document,
        bool writeIndented = true)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, CreateOptions(writeIndented))
            .ReplaceLineEndings("\n") + "\n";
    }

    public static UnrealApiBrowserDocument Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealApiBrowserDocument? document = JsonSerializer.Deserialize<UnrealApiBrowserDocument>(
            json,
            CreateOptions(writeIndented: true));
        Validate(document);
        return document!;
    }

    private static void Validate(UnrealApiBrowserDocument? document)
    {
        if (document is null ||
            document.SchemaVersion != UnrealApiBrowserDocument.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(document.ContractName) ||
            string.IsNullOrWhiteSpace(document.ManagedNamespace) ||
            document.Sources is null ||
            document.Summary is null ||
            document.Types is null ||
            document.Diagnostics is null ||
            document.Sources.InputSha256.Length != 64 ||
            document.Sources.InputSha256.Any(static character => !Uri.IsHexDigit(character)) ||
            document.Summary.TypeCount != document.Types.Count ||
            document.Summary.NodeCount != document.Types.Sum(static type => type.Members.Count) ||
            document.Summary.DiagnosticCount != document.Diagnostics.Count ||
            document.Types.SelectMany(static type => type.Members)
                .Select(static member => member.Id)
                .Distinct(StringComparer.Ordinal).Count() != document.Summary.NodeCount)
        {
            throw new JsonException("The Unreal API browser document is inconsistent.");
        }
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        JsonSerializerOptions options = UnifiedJson.CreateOptions(writeIndented);
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
