using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnifiedScript.Core;
using UnifiedScript.Core.Hir;
using UnifiedScript.CSharp;
using UnifiedScript.Semantics;

namespace UnifiedScript.Runtime;

public sealed record RoundTripExecutionCase(
    string Id,
    IReadOnlyList<ExecutionArgument> Arguments);

public sealed record RoundTripMethodRequirement(
    string QualifiedName,
    IReadOnlyList<RoundTripExecutionCase> Cases);

public sealed record RoundTripExecutionCaseDocument(
    string Id,
    IReadOnlyList<JsonElement> Arguments);

public sealed record RoundTripMethodRequirementDocument(
    string QualifiedName,
    IReadOnlyList<RoundTripExecutionCaseDocument> Cases);

public sealed record RoundTripRequirementDocument(
    string SchemaVersion,
    IReadOnlyList<RoundTripMethodRequirementDocument> Methods)
{
    public const string CurrentSchemaVersion = "unifiedscript.round-trip-requirements/v0.1";

    public IReadOnlyList<RoundTripMethodRequirement> ToRequirements() => Methods
        .Select(static method => new RoundTripMethodRequirement(
            method.QualifiedName,
            method.Cases.Select(static testCase => new RoundTripExecutionCase(
                testCase.Id,
                testCase.Arguments.Select(argument =>
                    new ExecutionArgument(argument.GetRawText())).ToArray())).ToArray()))
        .ToArray();
}

public sealed record RoundTripExecutionComparison(
    string Id,
    string VisualResult,
    string CodeResult,
    bool Equivalent);

public sealed record RoundTripMethodEquivalence(
    string QualifiedName,
    SemanticId VisualMethodId,
    SemanticId CodeMethodId,
    bool IdentityEquivalent,
    string VisualSemanticDigest,
    string CodeSemanticDigest,
    bool SemanticEquivalent,
    string VisualIlDigest,
    string CodeIlDigest,
    bool IlEquivalent,
    IReadOnlyList<RoundTripExecutionComparison> Executions,
    bool Passed);

public sealed record RoundTripEquivalenceReport(
    string SchemaVersion,
    bool SemanticConfigurationEquivalent,
    string VisualAssetDigest,
    string CodeAssetDigest,
    bool AssetEquivalent,
    IReadOnlyList<RoundTripMethodEquivalence> Methods,
    bool Passed)
{
    public const string CurrentSchemaVersion = "unifiedscript.round-trip-equivalence/v0.1";
}

public static class RoundTripRequirementJson
{
    public static RoundTripRequirementDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RoundTripRequirementDocument document = JsonSerializer.Deserialize<RoundTripRequirementDocument>(
            json,
            UnifiedJson.CreateOptions(writeIndented: false)) ??
            throw new JsonException("Round-trip requirements cannot be null.");
        if (document.SchemaVersion != RoundTripRequirementDocument.CurrentSchemaVersion ||
            document.Methods is null || document.Methods.Count == 0 ||
            document.Methods.Any(static method =>
                string.IsNullOrWhiteSpace(method.QualifiedName) ||
                method.Cases is null || method.Cases.Count == 0 ||
                method.Cases.Any(static testCase =>
                    string.IsNullOrWhiteSpace(testCase.Id) || testCase.Arguments is null)) ||
            document.Methods.Select(static method => method.QualifiedName)
                .Distinct(StringComparer.Ordinal).Count() != document.Methods.Count ||
            document.Methods.Any(static method =>
                method.Cases.Select(static testCase => testCase.Id)
                    .Distinct(StringComparer.Ordinal).Count() != method.Cases.Count))
        {
            throw new JsonException(
                "Round-trip requirements must use the current schema and contain unique methods and cases.");
        }
        return document;
    }

    public static string Serialize(
        RoundTripRequirementDocument document,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = Deserialize(JsonSerializer.Serialize(
            document,
            UnifiedJson.CreateOptions(writeIndented: false)));
        return JsonSerializer.Serialize(document, UnifiedJson.CreateOptions(writeIndented));
    }
}

public static class RoundTripEquivalenceJson
{
    public static string Serialize(
        RoundTripEquivalenceReport report,
        bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != RoundTripEquivalenceReport.CurrentSchemaVersion ||
            report.Methods is null || report.Methods.Count == 0)
        {
            throw new ArgumentException("Round-trip report is incomplete.", nameof(report));
        }
        return JsonSerializer.Serialize(report, UnifiedJson.CreateOptions(writeIndented));
    }
}

/// <summary>
/// Proves that a Visual-authored program and a later C# edit still describe the
/// same executable program. Source text and debug locations are deliberately not
/// compared; stable semantic identity, bound semantics, IL, execution results,
/// and Script Asset declarations are.
/// </summary>
public sealed class RoundTripEquivalenceVerifier
{
    private readonly CSharpProgramEmitter emitter = new();
    private readonly CoreClrExecutionHost executionHost = new();

    public RoundTripEquivalenceReport Verify(
        CSharpCompilationRequest visualRequest,
        CSharpCompilationRequest codeRequest,
        IEnumerable<RoundTripMethodRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(visualRequest);
        ArgumentNullException.ThrowIfNull(codeRequest);
        ArgumentNullException.ThrowIfNull(requirements);

        RoundTripMethodRequirement[] requestedMethods = requirements.ToArray();
        if (requestedMethods.Length == 0 ||
            requestedMethods.Any(static requirement =>
                string.IsNullOrWhiteSpace(requirement.QualifiedName) ||
                requirement.Cases is null ||
                requirement.Cases.Any(static testCase =>
                    string.IsNullOrWhiteSpace(testCase.Id) || testCase.Arguments is null)) ||
            requestedMethods.Select(static requirement => requirement.QualifiedName)
                .Distinct(StringComparer.Ordinal).Count() != requestedMethods.Length)
        {
            throw new ArgumentException(
                "Round-trip verification requires unique method names and valid execution cases.",
                nameof(requirements));
        }

        CSharpProgramImage visualImage = Emit(visualRequest);
        CSharpProgramImage codeImage = Emit(codeRequest);
        using CoreClrExecutionSession visualSession = executionHost.Load(
            visualImage,
            visualRequest.RuntimeReferencePaths);
        using CoreClrExecutionSession codeSession = executionHost.Load(
            codeImage,
            codeRequest.RuntimeReferencePaths);

        RoundTripMethodEquivalence[] methods = requestedMethods
            .Select(requirement => VerifyMethod(
                visualImage,
                codeImage,
                visualSession,
                codeSession,
                requirement))
            .ToArray();
        string visualAssetDigest = CreateAssetDigest(visualImage.ScriptAssets);
        string codeAssetDigest = CreateAssetDigest(codeImage.ScriptAssets);
        bool semanticConfigurationEquivalent =
            visualImage.Semantics.ConfigurationId == codeImage.Semantics.ConfigurationId;
        bool assetEquivalent = string.Equals(
            visualAssetDigest,
            codeAssetDigest,
            StringComparison.Ordinal);
        return new RoundTripEquivalenceReport(
            RoundTripEquivalenceReport.CurrentSchemaVersion,
            semanticConfigurationEquivalent,
            visualAssetDigest,
            codeAssetDigest,
            assetEquivalent,
            methods,
            semanticConfigurationEquivalent && assetEquivalent &&
                methods.All(static method => method.Passed));
    }

    private CSharpProgramImage Emit(CSharpCompilationRequest request)
    {
        CSharpProgramEmitResult result = emitter.Emit(request);
        return result.Succeeded && result.Image is not null
            ? result.Image
            : throw new ExecutionCompilationException(result.Diagnostics);
    }

    private static RoundTripMethodEquivalence VerifyMethod(
        CSharpProgramImage visualImage,
        CSharpProgramImage codeImage,
        CoreClrExecutionSession visualSession,
        CoreClrExecutionSession codeSession,
        RoundTripMethodRequirement requirement)
    {
        SemanticId visualMethodId = visualSession.ResolveVisualMethodId(requirement.QualifiedName);
        SemanticId codeMethodId = codeSession.ResolveVisualMethodId(requirement.QualifiedName);
        bool identityEquivalent = visualMethodId == codeMethodId;
        string visualSemanticDigest = CreateSemanticDigest(visualImage, visualMethodId);
        string codeSemanticDigest = CreateSemanticDigest(codeImage, codeMethodId);
        bool semanticEquivalent = string.Equals(
            visualSemanticDigest,
            codeSemanticDigest,
            StringComparison.Ordinal);
        string visualIlDigest = CreateIlDigest(visualImage, visualMethodId);
        string codeIlDigest = CreateIlDigest(codeImage, codeMethodId);
        bool ilEquivalent = string.Equals(visualIlDigest, codeIlDigest, StringComparison.Ordinal);
        RoundTripExecutionComparison[] executions = requirement.Cases
            .Select(testCase =>
            {
                ExecutionResult visual = visualSession.Execute(new ExecutionInvocation(
                    visualMethodId,
                    testCase.Arguments));
                ExecutionResult code = codeSession.Execute(new ExecutionInvocation(
                    codeMethodId,
                    testCase.Arguments));
                string visualResult = NormalizeExecutionResult(visual);
                string codeResult = NormalizeExecutionResult(code);
                return new RoundTripExecutionComparison(
                    testCase.Id,
                    visualResult,
                    codeResult,
                    string.Equals(visualResult, codeResult, StringComparison.Ordinal));
            })
            .ToArray();
        bool passed = identityEquivalent && semanticEquivalent && ilEquivalent &&
            executions.All(static execution => execution.Equivalent);
        return new RoundTripMethodEquivalence(
            requirement.QualifiedName,
            visualMethodId,
            codeMethodId,
            identityEquivalent,
            visualSemanticDigest,
            codeSemanticDigest,
            semanticEquivalent,
            visualIlDigest,
            codeIlDigest,
            ilEquivalent,
            executions,
            passed);
    }

    private static string CreateSemanticDigest(
        CSharpProgramImage image,
        SemanticId methodId)
    {
        HirMethodDeclaration method = image.Hir.Types
            .SelectMany(static type => type.Methods)
            .Single(candidate => candidate.Id == methodId);
        MethodDescriptor descriptor = image.Semantics.Methods.Single(candidate =>
            candidate.Id == methodId);
        HirNode[] methodNodes = method.DescendantsAndSelf().ToArray();
        Dictionary<SemanticId, int> methodNodeIndexes = methodNodes
            .Select(static (node, index) => (node.Id, index))
            .ToDictionary(static item => item.Id, static item => item.index);
        HashSet<SemanticId> methodNodeIds = methodNodeIndexes.Keys.ToHashSet();
        var canonical = new StringBuilder()
            .Append("unifiedscript.bound-method/v0.4|")
            .Append(descriptor.MetadataName).Append('|')
            .Append(descriptor.ContainingTypeId.Value).Append('|')
            .Append(descriptor.ReturnTypeId.Value).Append('|')
            .Append(descriptor.IsStatic).Append('|')
            .Append((int)descriptor.BodyRepresentation).Append('|')
            .Append((int)descriptor.Effects);
        foreach (ParameterDescriptor parameter in descriptor.Parameters)
        {
            canonical.Append("|p:").Append(parameter.Name).Append(':')
                .Append(parameter.TypeId.Value);
        }
        foreach (BoundNodeInfo node in image.Semantics.Nodes
                     .Where(node => methodNodeIds.Contains(node.NodeId))
                     .OrderBy(node => methodNodeIndexes[node.NodeId]))
        {
            canonical.Append("|n:").Append(methodNodeIndexes[node.NodeId])
                .Append(':').Append(node.TypeId?.Value)
                .Append(':').Append(NormalizeSymbol(node.DeclaredSymbolId, methodNodeIndexes))
                .Append(':').Append(NormalizeSymbol(node.ReferencedSymbolId, methodNodeIndexes))
                .Append(':').Append(node.InvokedMethodId?.Value)
                .Append(':').Append((int)node.Effects)
                .Append(':').Append((int)node.NullFlowState);
        }
        foreach (BoundForEachInfo loop in image.Semantics.ForEachLoops
                     .Where(loop => methodNodeIds.Contains(loop.NodeId))
                     .OrderBy(loop => methodNodeIndexes[loop.NodeId]))
        {
            canonical.Append("|f:").Append(methodNodeIndexes[loop.NodeId])
                .Append(':').Append((int)loop.Strategy)
                .Append(':').Append((int)loop.IterationKind)
                .Append(':').Append(loop.CollectionTypeId.Value)
                .Append(':').Append(loop.ElementTypeId.Value)
                .Append(':').Append(loop.EnumeratorTypeId?.Value)
                .Append(':').Append(loop.GetEnumeratorMethodId?.Value)
                .Append(':').Append(loop.MoveNextMethodId?.Value)
                .Append(':').Append(loop.CurrentGetterMethodId?.Value)
                .Append(':').Append(loop.DisposeMethodId?.Value)
                .Append(':').Append((int)loop.DisposalKind);
        }
        return Digest(canonical.ToString());
    }

    private static string? NormalizeSymbol(
        SemanticId? symbolId,
        IReadOnlyDictionary<SemanticId, int> methodNodeIndexes) =>
        symbolId is { } id && methodNodeIndexes.TryGetValue(id, out int index)
            ? $"node:{index}"
            : symbolId?.Value;

    private static string CreateIlDigest(CSharpProgramImage image, SemanticId methodId)
    {
        CSharpMethodSourceMap method = image.SourceMap.Methods.Single(candidate =>
            candidate.MethodId == methodId);
        using var stream = new MemoryStream(image.PortableExecutable, writable: false);
        using var peReader = new PEReader(stream);
        EntityHandle entity = MetadataTokens.EntityHandle(method.MetadataToken);
        if (entity.Kind != HandleKind.MethodDefinition)
        {
            throw new InvalidDataException(
                $"Source-map token '0x{method.MetadataToken:x8}' is not a method definition.");
        }
        MethodDefinition definition = peReader.GetMetadataReader()
            .GetMethodDefinition((MethodDefinitionHandle)entity);
        MethodBodyBlock body = peReader.GetMethodBody(definition.RelativeVirtualAddress);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "unifiedscript.method-il/v0.1");
        Append(hash, body.MaxStack);
        Append(hash, body.LocalVariablesInitialized ? 1 : 0);
        Append(hash, MetadataTokens.GetToken(body.LocalSignature));
        Append(hash, body.GetILBytes());
        foreach (ExceptionRegion region in body.ExceptionRegions)
        {
            Append(hash, (int)region.Kind);
            Append(hash, region.TryOffset);
            Append(hash, region.TryLength);
            Append(hash, region.HandlerOffset);
            Append(hash, region.HandlerLength);
            Append(hash, region.FilterOffset);
            Append(hash, MetadataTokens.GetToken(region.CatchType));
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CreateAssetDigest(ScriptAssetDocument document)
    {
        var canonical = new StringBuilder("unifiedscript.asset-contract/v0.1|")
            .Append(document.SemanticConfigurationId.Value);
        foreach (ScriptAssetDeclaration asset in document.Assets
                     .OrderBy(static asset => asset.Id.Value, StringComparer.Ordinal))
        {
            canonical.Append("|a:").Append(asset.Id.Value)
                .Append(':').Append(asset.QualifiedName)
                .Append(':').Append(asset.AssetPath);
            foreach (ScriptStateDeclaration state in asset.States
                         .OrderBy(static state => state.Id.Value, StringComparer.Ordinal))
            {
                canonical.Append("|s:").Append(state.Id.Value)
                    .Append(':').Append(state.Name)
                    .Append(':').Append(state.TypeMetadataName)
                    .Append(':').Append((int)state.TypeKind)
                    .Append(':').Append(state.DefaultValue.GetRawText())
                    .Append(':').Append(state.Exposure.InstanceEditable)
                    .Append(':').Append(state.Exposure.SaveGame)
                    .Append(':').Append(state.Exposure.Replicated)
                    .Append(':').Append(state.Exposure.Category);
            }
            foreach (ScriptFunctionDeclaration function in asset.Functions
                         .OrderBy(static function => function.Id.Value, StringComparer.Ordinal))
            {
                canonical.Append("|m:").Append(function.Id.Value)
                    .Append(':').Append(function.Name)
                    .Append(':').Append(function.QualifiedName)
                    .Append(':').Append(function.ReturnTypeMetadataName)
                    .Append(':').Append(function.IsStatic);
                foreach (ScriptParameterDeclaration parameter in function.Parameters)
                {
                    canonical.Append("|q:").Append(parameter.Id.Value)
                        .Append(':').Append(parameter.Name)
                        .Append(':').Append(parameter.TypeMetadataName);
                }
            }
        }
        return Digest(canonical.ToString());
    }

    private static string NormalizeExecutionResult(ExecutionResult result) => result.Outcome switch
    {
        ExecutionOutcome.Returned when result.ReturnValue is not null =>
            $"returned:{result.ReturnValue.TypeMetadataName}:{result.ReturnValue.Json}",
        ExecutionOutcome.Faulted when result.Fault is not null =>
            $"faulted:{result.Fault.TypeMetadataName}:{result.Fault.Message}",
        _ => throw new InvalidDataException("Execution result has an invalid outcome payload."),
    };

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void Append(IncrementalHash hash, string value) =>
        Append(hash, Encoding.UTF8.GetBytes(value));

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
