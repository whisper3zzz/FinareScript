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

        Assert.Single(
            diagnostics,
            static diagnostic =>
                diagnostic.Code == VisualProfileDiagnosticCodes.BreakTargetRequired);
        Assert.Single(
            diagnostics,
            static diagnostic =>
                diagnostic.Code == VisualProfileDiagnosticCodes.LoopControlTargetRequired);
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

    [Fact]
    public void Validate_AllowsRefReadonlyForEachButRejectsMutableRefAndAwaitVariants()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            class Example
            {
                [Visual]
                void Run(Span<int> writable, ReadOnlySpan<int> borrowed, IAsyncEnumerable<int> asyncValues)
                {
                    foreach (ref readonly int value in borrowed) { }
                    foreach (ref int value in writable) { }
                    await foreach (int value in asyncValues) { }
                }
            }
            """;

        IReadOnlyList<UnifiedScript.Core.UnifiedDiagnostic> diagnostics =
            new VisualProfileValidator().Validate(
                CSharpSyntaxTree.ParseText(source, path: "ForEachProfile.cs"));
        UnifiedScript.Core.UnifiedDiagnostic[] statements = diagnostics
            .Where(static diagnostic =>
                diagnostic.Code == VisualProfileDiagnosticCodes.StatementNotSupported)
            .ToArray();

        Assert.Equal(2, statements.Length);
        Assert.Contains(statements, static diagnostic =>
            diagnostic.Message.Contains("Mutable ref foreach", StringComparison.Ordinal));
        Assert.Contains(statements, static diagnostic =>
            diagnostic.Message.Contains("Await foreach", StringComparison.Ordinal));
    }
}
