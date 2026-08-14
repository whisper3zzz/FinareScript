using UnifiedScript.CSharp;
using UnifiedScript.Core.Hir;

namespace UnifiedScript.Tests;

public sealed class CSharpToHirLowererTests
{
    private const string SupportedSource = """
        namespace Game;

        class DoorController
        {
            [Visual]
            public void BeginPlay()
            {
                bool ready = Target != null;
                if (ready)
                {
                    Target.Open();
                }
                return;
            }
        }
        """;

    [Fact]
    public void Lower_ProducesTypedHierarchyForSupportedMethod()
    {
        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(SupportedSource, "DoorController.cs");

        Assert.True(result.Succeeded);
        HirTypeDeclaration type = Assert.Single(result.CompilationUnit!.Types);
        Assert.Equal("Game.DoorController", type.QualifiedName);
        HirMethodDeclaration method = Assert.Single(type.Methods);
        Assert.Equal("BeginPlay", method.Name);
        Assert.Collection(
            method.Body.Statements,
            statement => Assert.IsType<HirVariableDeclarationStatement>(statement),
            statement => Assert.IsType<HirIfStatement>(statement),
            statement => Assert.IsType<HirReturnStatement>(statement));
    }

    [Fact]
    public void Lower_IsDeterministicForIdenticalSource()
    {
        CSharpLoweringResult first = new CSharpToHirLowerer().Lower(SupportedSource, "DoorController.cs");
        CSharpLoweringResult second = new CSharpToHirLowerer().Lower(SupportedSource, "DoorController.cs");

        Assert.Equal(first.CompilationUnit!.Id, second.CompilationUnit!.Id);
        Assert.Equal(
            first.CompilationUnit.Types[0].Methods[0].Body.Statements.Select(static statement => statement.Id),
            second.CompilationUnit.Types[0].Methods[0].Body.Statements.Select(static statement => statement.Id));
    }

    [Fact]
    public void Lower_PreservesExecutionRelevantMethodModifiersAndGenericReturnType()
    {
        const string source = """
            class Loader
            {
                [Visual]
                public static async Task<int> Load()
                {
                    return await Fetch();
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "Loader.cs");

        Assert.True(result.Succeeded);
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        Assert.True(method.IsStatic);
        Assert.True(method.IsAsync);
        Assert.Equal("Task<int>", method.ReturnType);
        Assert.IsType<HirAwaitExpression>(Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements)).Expression);
    }

    [Fact]
    public void Lower_RejectsUnsupportedGenericInvocationBeforeLowering()
    {
        const string source = """
            class Example
            {
                [Visual]
                void Run()
                {
                    Invoke<int>();
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "Example.cs");

        Assert.False(result.Succeeded);
        Assert.Null(result.CompilationUnit);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == VisualProfileDiagnosticCodes.ExpressionNotSupported);
    }
}
