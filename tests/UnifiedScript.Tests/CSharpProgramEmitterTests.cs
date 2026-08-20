using System.Security.Cryptography;
using UnifiedScript.CSharp;
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
        Assert.Equal(firstImage.Hir.Id, firstImage.Semantics.SourceCompilationId);
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
}
