using Microsoft.CodeAnalysis.CSharp;
using UnifiedScript.CSharp;

namespace UnifiedScript.Tests;

public sealed class VisualProfileValidatorTests
{
    [Fact]
    public void Validate_RejectsDynamicAndLambdaWhileAllowingWhileLoop()
    {
        const string source = """
            class Example
            {
                [Visual]
                void Run(dynamic value)
                {
                    System.Func<int, int> increment = x => x + 1;
                    while (true) { }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, path: "Example.cs");

        var diagnostics = new VisualProfileValidator().Validate(tree);
        var codes = diagnostics.Select(static diagnostic => diagnostic.Code).ToHashSet();

        Assert.Contains(VisualProfileDiagnosticCodes.DynamicNotSupported, codes);
        Assert.Contains(VisualProfileDiagnosticCodes.LambdaNotSupported, codes);
        Assert.DoesNotContain(VisualProfileDiagnosticCodes.StatementNotSupported, codes);
    }

    [Fact]
    public void Validate_AllowsBreakAndContinueOnlyInsideWhile()
    {
        const string valid = """
            class Example
            {
                [Visual]
                void Run(bool ready)
                {
                    while (ready)
                    {
                        if (ready) continue;
                        break;
                    }
                }
            }
            """;
        const string invalid = """
            class Example
            {
                [Visual]
                void Run()
                {
                    break;
                    continue;
                }
            }
            """;

        Assert.Empty(new VisualProfileValidator().Validate(
            CSharpSyntaxTree.ParseText(valid, path: "ValidLoop.cs")));
        IReadOnlyList<UnifiedScript.Core.UnifiedDiagnostic> diagnostics =
            new VisualProfileValidator().Validate(
                CSharpSyntaxTree.ParseText(invalid, path: "InvalidLoop.cs"));

        Assert.Equal(
            2,
            diagnostics.Count(static diagnostic =>
                diagnostic.Code == VisualProfileDiagnosticCodes.LoopControlTargetRequired));
    }

    [Fact]
    public void Validate_IgnoresNonVisualMethods()
    {
        const string source = """
            class Example
            {
                void TextOnly()
                {
                    dynamic value = 1;
                    while (true) { }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, path: "Example.cs");

        Assert.Empty(new VisualProfileValidator().Validate(tree));
    }

    [Fact]
    public void Validate_RejectsSyntaxThatWouldLoseCallOrDeclarationSemantics()
    {
        const string source = """
            class Example
            {
                [Visual]
                void Run(int amount = 1)
                {
                    const int local = 2;
                    Invoke<int>(value: local);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, path: "Example.cs");

        var codes = new VisualProfileValidator()
            .Validate(tree)
            .Select(static diagnostic => diagnostic.Code)
            .ToHashSet();

        Assert.Contains(VisualProfileDiagnosticCodes.DefaultParameterValueNotSupported, codes);
        Assert.Contains(VisualProfileDiagnosticCodes.LocalModifierNotSupported, codes);
        Assert.Contains(VisualProfileDiagnosticCodes.NamedArgumentNotSupported, codes);
        Assert.Contains(VisualProfileDiagnosticCodes.ExpressionNotSupported, codes);
    }
}
