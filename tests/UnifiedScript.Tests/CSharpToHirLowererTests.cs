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

    [Fact]
    public void Lower_ProducesExplicitWhileBreakAndContinueHierarchy()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    while (value > 0)
                    {
                        if (value == 2)
                        {
                            value = value - 1;
                            continue;
                        }

                        if (value == 1)
                        {
                            break;
                        }

                        value = value - 1;
                    }

                    return value;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "LoopRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirWhileStatement loop = Assert.IsType<HirWhileStatement>(method.Body.Statements[0]);
        Assert.IsType<HirBinaryExpression>(loop.Condition);
        Assert.Single(loop.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        Assert.Single(loop.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        Assert.IsType<HirReturnStatement>(method.Body.Statements[1]);
        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains(
            $"\"schemaVersion\": \"{UnifiedScript.Core.HirDocument.CurrentSchemaVersion}\"",
            json);
        Assert.Contains("\"kind\": \"while\"", json);
        Assert.Contains("\"kind\": \"break\"", json);
        Assert.Contains("\"kind\": \"continue\"", json);
    }

    [Fact]
    public void Lower_PreservesForClausesAndUnaryPositionWithoutWhileDesugaring()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int count)
                {
                    for (int i = 0, step = 1; i < count; i++, ++step)
                    {
                        if (i == 2)
                        {
                            continue;
                        }

                        if (step > count)
                        {
                            break;
                        }
                    }

                    return count;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "ForRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirForStatement loop = Assert.IsType<HirForStatement>(method.Body.Statements[0]);
        HirForVariableDeclaration declaration = Assert.IsType<HirForVariableDeclaration>(
            loop.Declaration);
        Assert.Equal("int", declaration.TypeName);
        Assert.Equal(["i", "step"], declaration.Variables.Select(static variable => variable.Name));
        Assert.All(declaration.Variables, static variable => Assert.NotNull(variable.Initializer));
        Assert.Empty(loop.Initializers);
        Assert.IsType<HirBinaryExpression>(loop.Condition);
        Assert.Collection(
            loop.Incrementors,
            incrementor => Assert.True(Assert.IsType<HirUnaryExpression>(incrementor).IsPostfix),
            incrementor => Assert.False(Assert.IsType<HirUnaryExpression>(incrementor).IsPostfix));
        Assert.Single(loop.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        Assert.Single(loop.Body.DescendantsAndSelf().OfType<HirBreakStatement>());
        Assert.DoesNotContain(method.Body.DescendantsAndSelf(), static node => node is HirWhileStatement);

        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains("\"schemaVersion\": \"unifiedscript.hir/v0.7\"", json);
        Assert.Contains("\"kind\": \"for\"", json);
        Assert.Contains("\"isPostfix\": true", json);
    }

    [Fact]
    public void Lower_PreservesDoWhileBodyBeforeConditionWithoutWhileDesugaring()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Run(int value)
                {
                    do
                    {
                        value = value - 1;
                        if (value == 2)
                        {
                            continue;
                        }
                    }
                    while (value > 0);

                    return value;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "DoWhileRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirDoWhileStatement loop = Assert.IsType<HirDoWhileStatement>(method.Body.Statements[0]);
        Assert.IsType<HirExpressionStatement>(loop.Body.Statements[0]);
        Assert.IsType<HirBinaryExpression>(loop.Condition);
        Assert.Single(loop.Body.DescendantsAndSelf().OfType<HirContinueStatement>());
        Assert.DoesNotContain(method.DescendantsAndSelf(), static node => node is HirWhileStatement);

        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains("\"kind\": \"doWhile\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Lower_PreservesForEachVariableCollectionAndBodyWithoutForDesugaring()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Sum(int[] values)
                {
                    int total = 0;
                    foreach (int value in values)
                    {
                        total = total + value;
                    }

                    return total;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(source, "ForEachRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirForEachStatement loop = Assert.IsType<HirForEachStatement>(method.Body.Statements[1]);
        Assert.Equal("value", loop.Variable.Name);
        Assert.Equal("int", loop.Variable.TypeName);
        Assert.Equal("values", Assert.IsType<HirIdentifierExpression>(loop.Collection).Name);
        Assert.Single(loop.Body.Statements);
        Assert.DoesNotContain(method.DescendantsAndSelf(), static node => node is HirForStatement);

        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains("\"schemaVersion\": \"unifiedscript.hir/v0.7\"", json);
        Assert.Contains("\"kind\": \"forEach\"", json);
    }

    [Fact]
    public void Lower_PostfixAdmissionDoesNotAdmitNullForgivingExpressions()
    {
        const string source = """
            #nullable enable

            static class Rules
            {
                [Visual]
                static string Run(string? value)
                {
                    return value!;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(
            source,
            "NullForgivingRules.cs");

        Assert.False(result.Succeeded);
        Assert.Null(result.CompilationUnit);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == VisualProfileDiagnosticCodes.ExpressionNotSupported &&
            diagnostic.Message.Contains("SuppressNullableWarningExpression", StringComparison.Ordinal));
    }

    [Fact]
    public void Lower_PreservesConditionalExpressionAsFirstClassHir()
    {
        const string source = """
            static class Rules
            {
                [Visual]
                static int Select(bool enabled, int whenEnabled, int fallback)
                {
                    return enabled ? whenEnabled : fallback;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(
            source,
            "ConditionalRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirReturnStatement returnStatement = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirConditionalExpression conditional = Assert.IsType<HirConditionalExpression>(
            returnStatement.Expression);
        Assert.Equal("enabled", Assert.IsType<HirIdentifierExpression>(conditional.Condition).Name);
        Assert.Equal("whenEnabled", Assert.IsType<HirIdentifierExpression>(conditional.WhenTrue).Name);
        Assert.Equal("fallback", Assert.IsType<HirIdentifierExpression>(conditional.WhenFalse).Name);

        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains("\"schemaVersion\": \"unifiedscript.hir/v0.7\"", json);
        Assert.Contains("\"kind\": \"conditional\"", json);
    }

    [Fact]
    public void Lower_PreservesNullConditionalAccessAndMemberBindingAsFirstClassHir()
    {
        const string source = """
            #nullable enable

            static class Rules
            {
                [Visual]
                static int? Measure(string? value)
                {
                    return value?.Length;
                }
            }
            """;

        CSharpLoweringResult result = new CSharpToHirLowerer().Lower(
            source,
            "NullConditionalRules.cs");

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        HirMethodDeclaration method = Assert.Single(Assert.Single(result.CompilationUnit!.Types).Methods);
        HirReturnStatement valueReturn = Assert.IsType<HirReturnStatement>(
            Assert.Single(method.Body.Statements));
        HirConditionalAccessExpression conditionalAccess =
            Assert.IsType<HirConditionalAccessExpression>(valueReturn.Expression);
        Assert.Equal(
            "value",
            Assert.IsType<HirIdentifierExpression>(conditionalAccess.Receiver).Name);
        Assert.Equal(
            "Length",
            Assert.IsType<HirConditionalMemberBindingExpression>(
                conditionalAccess.WhenNotNull).MemberName);

        string json = UnifiedScript.Core.HirJson.Serialize(
            UnifiedScript.Core.HirDocument.Create(result.CompilationUnit));
        Assert.Contains("\"schemaVersion\": \"unifiedscript.hir/v0.7\"", json);
        Assert.Contains("\"kind\": \"conditionalAccess\"", json);
        Assert.Contains("\"kind\": \"conditionalMemberBinding\"", json);
    }
}
