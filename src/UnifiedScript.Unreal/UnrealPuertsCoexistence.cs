using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnifiedScript.Unreal;

public static class UnrealPuertsRuntimeOwner
{
    public const string Puerts = "puerts";
    public const string UnifiedScript = "unifiedScript";

    public static bool IsValid(string value) => value is Puerts or UnifiedScript;
}

public sealed record UnrealPuertsCoexistenceRoute(
    string SourceType,
    string LifecycleKey,
    string Owner,
    string PuertsSourcePath,
    string? ManagedTypeName,
    IReadOnlyList<string> LifecycleHooks,
    IReadOnlyList<string> WorkItemIds);

public sealed record UnrealPuertsCoexistenceManifest(
    string SchemaVersion,
    string ManifestId,
    string ProjectName,
    string ScanFingerprint,
    string PlanId,
    string OwnershipPolicy,
    IReadOnlyList<UnrealPuertsCoexistenceRoute> Routes);

public sealed record UnrealPuertsCoexistenceGeneratedFile(
    string RelativePath,
    string MediaType,
    string Content,
    string Sha256);

public sealed record UnrealPuertsCoexistencePackage(
    UnrealPuertsCoexistenceManifest Manifest,
    IReadOnlyList<UnrealPuertsCoexistenceGeneratedFile> Files);

public static class UnrealPuertsCoexistenceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(UnrealPuertsCoexistenceManifest value) =>
        JsonSerializer.Serialize(value, Options).ReplaceLineEndings("\n") + "\n";

    public static UnrealPuertsCoexistenceManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        UnrealPuertsCoexistenceManifest value =
            JsonSerializer.Deserialize<UnrealPuertsCoexistenceManifest>(json, Options) ??
            throw new JsonException("Puerts coexistence manifest JSON is empty.");
        UnrealPuertsCoexistencePlanner.Validate(value);
        return value;
    }
}

public sealed class UnrealPuertsCoexistencePlanner
{
    public const string OwnershipPolicy =
        "exactly-one-runtime-owns-each-world-lifecycle";

    public UnrealPuertsCoexistencePackage Generate(
        UnrealPuertsMigrationPlan plan,
        IReadOnlyDictionary<string, string>? unifiedManagedTypes = null)
    {
        ValidatePlan(plan);
        var requestedUnifiedTypes = new Dictionary<string, string>(
            unifiedManagedTypes ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);
        UnrealPuertsMigrationWorkItem[] scriptTypes = plan.WorkItems
            .Where(static item => item.Category == "scriptType")
            .OrderBy(static item => SourceType(item.SourceKey), StringComparer.Ordinal)
            .ToArray();
        var knownTypes = new HashSet<string>(
            scriptTypes.Select(static item => SourceType(item.SourceKey)),
            StringComparer.Ordinal);
        string[] unknown = requestedUnifiedTypes.Keys
            .Where(type => !knownTypes.Contains(type))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"UnifiedScript ownership was requested for unknown source type(s): " +
                string.Join(", ", unknown),
                nameof(unifiedManagedTypes));
        }
        if (requestedUnifiedTypes.Any(static item => string.IsNullOrWhiteSpace(item.Value)))
        {
            throw new ArgumentException(
                "Every UnifiedScript-owned source type requires a managed type name.",
                nameof(unifiedManagedTypes));
        }

        UnrealPuertsCoexistenceRoute[] routes = scriptTypes.Select(typeItem =>
        {
            string sourceType = SourceType(typeItem.SourceKey);
            UnrealPuertsMigrationWorkItem[] lifecycleItems = plan.WorkItems
                .Where(item => item.Category == "lifecycleHook" &&
                    QualifiedOwner(item.SourceKey) == sourceType)
                .OrderBy(static item => item.SourceKey, StringComparer.Ordinal)
                .ToArray();
            bool unified = requestedUnifiedTypes.TryGetValue(
                sourceType,
                out string? managedTypeName);
            return new UnrealPuertsCoexistenceRoute(
                sourceType,
                LifecycleKey(plan.ProjectName, sourceType),
                unified ? UnrealPuertsRuntimeOwner.UnifiedScript : UnrealPuertsRuntimeOwner.Puerts,
                ResolveSourcePath(typeItem),
                unified ? managedTypeName : null,
                lifecycleItems.Select(static item => QualifiedMember(item.SourceKey)).ToArray(),
                new[] { typeItem.Id }
                    .Concat(lifecycleItems.Select(static item => item.Id))
                    .ToArray());
        }).ToArray();
        string routeIdentity = string.Join(
            "\n",
            routes.Select(static route =>
                $"{route.SourceType}\t{route.LifecycleKey}\t{route.Owner}\t" +
                $"{route.PuertsSourcePath}\t{route.ManagedTypeName}"));
        string manifestId = "coexistence-" + Hash(
            plan.PlanId + "\n" + routeIdentity)[7..23];
        var manifest = new UnrealPuertsCoexistenceManifest(
            "unifiedscript.puerts-coexistence/v0.1",
            manifestId,
            plan.ProjectName,
            plan.ScanFingerprint,
            plan.PlanId,
            OwnershipPolicy,
            routes);
        Validate(manifest);
        string manifestJson = UnrealPuertsCoexistenceJson.Serialize(manifest);
        string puertsGuard = GeneratePuertsGuard(manifest);
        return new UnrealPuertsCoexistencePackage(
            manifest,
            [
                CreateFile("coexistence-manifest.json", "application/json", manifestJson),
                CreateFile("PuertsOwnership.generated.ts", "text/typescript", puertsGuard),
            ]);
    }

    public static void Validate(UnrealPuertsCoexistenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != "unifiedscript.puerts-coexistence/v0.1" ||
            string.IsNullOrWhiteSpace(manifest.ManifestId) ||
            string.IsNullOrWhiteSpace(manifest.ProjectName) ||
            string.IsNullOrWhiteSpace(manifest.ScanFingerprint) ||
            string.IsNullOrWhiteSpace(manifest.PlanId) ||
            manifest.OwnershipPolicy != OwnershipPolicy ||
            manifest.Routes is null || manifest.Routes.Count == 0)
        {
            throw new ArgumentException("Puerts coexistence manifest is incomplete or unsupported.");
        }
        if (manifest.Routes.Select(static route => route.SourceType)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Routes.Count ||
            manifest.Routes.Select(static route => route.LifecycleKey)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Routes.Count)
        {
            throw new ArgumentException(
                "Puerts coexistence routes require unique source types and lifecycle keys.");
        }
        foreach (UnrealPuertsCoexistenceRoute route in manifest.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.SourceType) ||
                string.IsNullOrWhiteSpace(route.LifecycleKey) ||
                !UnrealPuertsRuntimeOwner.IsValid(route.Owner) ||
                string.IsNullOrWhiteSpace(route.PuertsSourcePath) ||
                route.LifecycleHooks is null || route.WorkItemIds is null ||
                route.WorkItemIds.Count == 0 ||
                route.Owner == UnrealPuertsRuntimeOwner.UnifiedScript &&
                string.IsNullOrWhiteSpace(route.ManagedTypeName) ||
                route.Owner == UnrealPuertsRuntimeOwner.Puerts && route.ManagedTypeName is not null)
            {
                throw new ArgumentException(
                    $"Puerts coexistence route '{route.SourceType}' is incomplete or invalid.");
            }
        }
    }

    private static string GeneratePuertsGuard(UnrealPuertsCoexistenceManifest manifest)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("import * as UE from 'ue';");
        source.AppendLine("import {$ref, $unref} from 'puerts';");
        source.AppendLine();
        source.AppendLine("export type UnifiedScriptLifecycleOwner = 'puerts' | 'unifiedScript';");
        source.AppendLine("export interface UnifiedScriptLifecycleRoute {");
        source.AppendLine("    readonly lifecycleKey: string;");
        source.AppendLine("    readonly owner: UnifiedScriptLifecycleOwner;");
        source.AppendLine("    readonly managedTypeName?: string;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("export const unifiedScriptLifecycleRoutes: Readonly<Record<string, UnifiedScriptLifecycleRoute>> = Object.freeze({");
        foreach (UnrealPuertsCoexistenceRoute route in manifest.Routes)
        {
            source.Append("    ").Append(JsonSerializer.Serialize(route.SourceType)).Append(": { lifecycleKey: ")
                .Append(JsonSerializer.Serialize(route.LifecycleKey)).Append(", owner: ")
                .Append(JsonSerializer.Serialize(route.Owner));
            if (route.ManagedTypeName is not null)
            {
                source.Append(", managedTypeName: ")
                    .Append(JsonSerializer.Serialize(route.ManagedTypeName));
            }
            source.AppendLine(" },");
        }
        source.AppendLine("});");
        source.AppendLine();
        source.AppendLine("function requirePuertsRoute(sourceType: string): UnifiedScriptLifecycleRoute {");
        source.AppendLine("    const route = unifiedScriptLifecycleRoutes[sourceType];");
        source.AppendLine("    if (!route) throw new Error(`No coexistence route exists for '${sourceType}'.`);");
        source.AppendLine("    if (route.owner !== 'puerts') throw new Error(`'${sourceType}' is owned by UnifiedScript.`);");
        source.AppendLine("    return route;");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("export function claimPuertsLifecycle(");
        source.AppendLine("    sourceType: string,");
        source.AppendLine("    worldContext: UE.Object,");
        source.AppendLine("    claimant: UE.Object,");
        source.AppendLine("    hostObject: UE.Object): void {");
        source.AppendLine("    const route = requirePuertsRoute(sourceType);");
        source.AppendLine("    const error = $ref<string>();");
        source.AppendLine("    if (!UE.UnifiedScriptWorldLibrary.ClaimGameplayLifecycle(");
        source.AppendLine("        worldContext, claimant, hostObject, route.lifecycleKey,");
        source.AppendLine("        UE.EUnifiedScriptRuntimeOwner.Puerts, error)) {");
        source.AppendLine("        throw new Error($unref(error));");
        source.AppendLine("    }");
        source.AppendLine("}");
        source.AppendLine();
        source.AppendLine("export function releasePuertsLifecycle(");
        source.AppendLine("    sourceType: string,");
        source.AppendLine("    worldContext: UE.Object,");
        source.AppendLine("    claimant: UE.Object,");
        source.AppendLine("    hostObject: UE.Object): void {");
        source.AppendLine("    const route = requirePuertsRoute(sourceType);");
        source.AppendLine("    const error = $ref<string>();");
        source.AppendLine("    if (!UE.UnifiedScriptWorldLibrary.ReleaseGameplayLifecycle(");
        source.AppendLine("        worldContext, claimant, hostObject, route.lifecycleKey,");
        source.AppendLine("        UE.EUnifiedScriptRuntimeOwner.Puerts, error)) {");
        source.AppendLine("        throw new Error($unref(error));");
        source.AppendLine("    }");
        source.AppendLine("}");
        return source.ToString().ReplaceLineEndings("\n");
    }

    private static void ValidatePlan(UnrealPuertsMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != "unifiedscript.puerts-migration-plan/v0.1" ||
            string.IsNullOrWhiteSpace(plan.ProjectName) ||
            string.IsNullOrWhiteSpace(plan.ScanFingerprint) ||
            string.IsNullOrWhiteSpace(plan.PlanId) ||
            plan.WorkItems is null)
        {
            throw new ArgumentException("Puerts migration plan is incomplete or unsupported.");
        }
    }

    private static string SourceType(string key)
    {
        int separator = key.IndexOf(" extends ", StringComparison.Ordinal);
        return separator < 0 ? key : key[..separator];
    }

    private static string QualifiedOwner(string key)
    {
        int separator = key.LastIndexOf('.');
        return separator < 0 ? key : key[..separator];
    }

    private static string QualifiedMember(string key)
    {
        int separator = key.LastIndexOf('.');
        return separator < 0 ? key : key[(separator + 1)..];
    }

    private static string ResolveSourcePath(UnrealPuertsMigrationWorkItem item) =>
        item.Locations.OrderBy(static location => location.SourcePath, StringComparer.Ordinal)
            .Select(static location => location.SourcePath)
            .FirstOrDefault() ?? throw new ArgumentException(
                $"Script type '{item.SourceKey}' has no source location.");

    private static string LifecycleKey(string projectName, string sourceType) =>
        SanitizeKey(projectName) + "." + SanitizeKey(sourceType);

    private static string SanitizeKey(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            result.Append(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character
                : '_');
        }
        return result.ToString();
    }

    private static UnrealPuertsCoexistenceGeneratedFile CreateFile(
        string relativePath,
        string mediaType,
        string content) =>
        new(relativePath, mediaType, content, Hash(content));

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
