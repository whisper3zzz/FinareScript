using Finare.Core;
using Finare.Core.Hir;
using Finare.Graph;
using Finare.Semantics;

namespace Finare.CSharp;

public sealed record CSharpProgramImage(
    string AssemblyName,
    byte[] PortableExecutable,
    byte[] PortablePdb,
    CSharpProgramSourceMap SourceMap,
    HirCompilationUnit Hir,
    BoundCompilation Semantics,
    ScriptAssetDocument ScriptAssets,
    IReadOnlyList<IdentitySidecarDocument> IdentitySidecars)
{
    // These init properties keep the original positional API intact while
    // carrying the exact graph/owner maps produced by this analysis.
    public GraphDocument? Graph { get; init; }

    public IReadOnlyDictionary<SemanticId, SemanticId> DeclarationOwners { get; init; } =
        new Dictionary<SemanticId, SemanticId>();
}
