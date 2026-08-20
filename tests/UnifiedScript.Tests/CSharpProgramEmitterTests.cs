using System.Security.Cryptography;
using UnifiedScript.CSharp;
using UnifiedScript.Core.Hir;
using Core = UnifiedScript.Core;

namespace UnifiedScript.Tests;

public sealed class CSharpProgramEmitterTests
{
    private const string Source = """
        using UnifiedScript;

        namespace Tests.Execution;

        public static class Rules
        {
            [Visual]
            public static int Add(int left, int right)
            {
                return left + right;
            }
        }
        """;

    [Fact]
    public void Emit_ProducesDeterministicPortableExecutableAndPdb()
    {
        CSharpCompilationRequest request = CSharpCompilationRequest.Single(
            Source,
            "Tests/Execution/Rules.cs");
        var emitter = new CSharpProgramEmitter();

        CSharpProgramEmitResult first = emitter.Emit(request);
        CSharpProgramEmitResult second = emitter.Emit(request);

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(second.Succeeded, string.Join(Environment.NewLine, second.Diagnostics));
        CSharpProgramImage firstImage = Assert.IsType<CSharpProgramImage>(first.Image);
        CSharpProgramImage secondImage = Assert.IsType<CSharpProgramImage>(second.Image);
        Assert.NotEmpty(firstImage.PortableExecutable);
        Assert.NotEmpty(firstImage.PortablePdb);
        Assert.Equal(
            SHA256.HashData(firstImage.PortableExecutable),
            SHA256.HashData(secondImage.PortableExecutable));
        Assert.Equal(
            SHA256.HashData(firstImage.PortablePdb),
            SHA256.HashData(secondImage.PortablePdb));
        Assert.Equal(
            CSharpProgramSourceMapJson.Serialize(firstImage.SourceMap),
            CSharpProgramSourceMapJson.Serialize(secondImage.SourceMap));
        Assert.Equal(firstImage.Hir.Id, firstImage.Semantics.SourceCompilationId);
        Assert.Equal(firstImage.Hir.Id, firstImage.ScriptAssets.SourceCompilationId);
        Assert.Equal(
            Core.ScriptAssetJson.Serialize(firstImage.ScriptAssets),
            Core.ScriptAssetJson.Serialize(secondImage.ScriptAssets));
        CSharpMethodSourceMap method = Assert.Single(firstImage.SourceMap.Methods);
        Assert.Equal(0x06000001, method.MetadataToken);
        Assert.NotNull(method.EntryIlOffset);
        Assert.NotEmpty(method.SequencePoints);
        HirReturnStatement returnStatement = Assert.Single(
            firstImage.Hir.DescendantsAndSelf().OfType<HirReturnStatement>());
        CSharpNodeIlMap returnMap = Assert.Single(method.Nodes, node =>
            node.NodeId == returnStatement.Id);
        Assert.Contains(method.SequencePoints, point =>
            returnMap.IlOffsets.Contains(point.IlOffset) &&
            point.Span.Start.Line == returnStatement.Span.Start.Line);
    }

    [Fact]
    public void Emit_RefusesAProgramOutsideTheVisualProfile()
    {
        const string source = """
            using UnifiedScript;

            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    switch (value)
                    {
                        default:
                            return value;
                    }
                }
            }
            """;

        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            CSharpCompilationRequest.Single(source, "Unsupported.cs"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Image);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Severity == Core.UnifiedDiagnosticSeverity.Error);
    }

    [Fact]
    public void Emit_ResolvesSameArityOverloadsByExactMetadataSignature()
    {
        const string source = """
            using UnifiedScript;

            namespace Tests.Execution;

            public static class OverloadedRules
            {
                [Visual]
                public static int Echo(int value)
                {
                    return value;
                }

                [Visual]
                public static string Echo(string value)
                {
                    return value;
                }
            }
            """;

        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            CSharpCompilationRequest.Single(source, "Tests/Execution/OverloadedRules.cs"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        CSharpProgramSourceMap sourceMap = Assert.IsType<CSharpProgramImage>(result.Image).SourceMap;
        Assert.Equal(2, sourceMap.Methods.Count);
        Assert.Contains(sourceMap.Methods, static method =>
            method.MetadataName == "Tests.Execution.OverloadedRules.Echo(System.Int32)");
        Assert.Contains(sourceMap.Methods, static method =>
            method.MetadataName == "Tests.Execution.OverloadedRules.Echo(System.String)");
        Assert.Equal(2, sourceMap.Methods.Select(static method => method.MetadataToken).Distinct().Count());
    }

    [Fact]
    public void Emit_MapsNullableValueAndReferenceSignaturesAfterClrTypeErasure()
    {
        const string source = """
            using UnifiedScript;

            namespace Tests.Execution;

            public static class NullableRules
            {
                [Visual]
                public static int? Measure(string? value)
                {
                    return value?.Length;
                }
            }
            """;

        CSharpProgramEmitResult result = new CSharpProgramEmitter().Emit(
            CSharpCompilationRequest.Single(source, "Tests/Execution/NullableRules.cs"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        CSharpMethodSourceMap method = Assert.Single(
            Assert.IsType<CSharpProgramImage>(result.Image).SourceMap.Methods);
        Assert.Equal(
            "Tests.Execution.NullableRules.Measure(System.String?)",
            method.MetadataName);
        Assert.NotEmpty(method.SequencePoints);
        Assert.NotEmpty(method.Nodes);
    }
}
