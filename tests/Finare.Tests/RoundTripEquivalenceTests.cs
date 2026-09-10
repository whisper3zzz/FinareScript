using Finare.Core;
using Finare.CSharp;
using Finare.Runtime;
using System.Text.Json;

namespace Finare.Tests;

public sealed class RoundTripEquivalenceTests
{
    private const string VisualSource = """
        using Finare;

        namespace Tests.RoundTrip;

        public static class ShotPolicy
        {
            [Visual]
            public static bool ShouldFire(float shootInput, bool canShoot)
            {
                bool thresholdReached = shootInput >= 0.6f;
                return thresholdReached && canShoot;
            }
        }
        """;

    private const string EquivalentCodeSource = """
        using Finare;

        namespace Tests.RoundTrip;

        public static class ShotPolicy
        {
            [Visual]
            public static bool ShouldFire(float shootInput, bool canShoot)
            {
                bool fireRequested = shootInput >= 0.60f;
                return fireRequested && canShoot;
            }
        }
        """;

    private const string ChangedCodeSource = """
        using Finare;

        namespace Tests.RoundTrip;

        public static class ShotPolicy
        {
            [Visual]
            public static bool ShouldFire(float shootInput, bool canShoot)
            {
                bool fireRequested = shootInput >= 0.7f;
                return fireRequested && canShoot;
            }
        }
        """;

    [Fact]
    public void EquivalentVisualAndCodeProgramsMatchIdentitySemanticsIlAssetsAndResults()
    {
        IdentitySidecarDocument identity = InitializeIdentity(VisualSource);
        CSharpCompilationRequest visual = Request(VisualSource, identity);
        CSharpCompilationRequest code = Request(EquivalentCodeSource, identity);

        RoundTripEquivalenceReport report = new RoundTripEquivalenceVerifier().Verify(
            visual,
            code,
            [Requirement()]);

        Assert.True(report.Passed, JsonSerializer.Serialize(report));
        Assert.True(report.SemanticConfigurationEquivalent);
        Assert.True(report.AssetEquivalent);
        RoundTripMethodEquivalence method = Assert.Single(report.Methods);
        Assert.True(method.IdentityEquivalent);
        Assert.True(method.SemanticEquivalent, JsonSerializer.Serialize(report));
        Assert.True(method.IlEquivalent);
        Assert.All(method.Executions, static execution => Assert.True(execution.Equivalent));
    }

    [Fact]
    public void BehaviorChangeFailsIlAndExecutionEquivalence()
    {
        IdentitySidecarDocument identity = InitializeIdentity(VisualSource);
        CSharpCompilationRequest visual = Request(VisualSource, identity);
        CSharpCompilationRequest changed = Request(ChangedCodeSource, identity);

        RoundTripEquivalenceReport report = new RoundTripEquivalenceVerifier().Verify(
            visual,
            changed,
            [Requirement()]);

        Assert.False(report.Passed);
        RoundTripMethodEquivalence method = Assert.Single(report.Methods);
        Assert.True(method.IdentityEquivalent);
        Assert.True(method.SemanticEquivalent);
        Assert.False(method.IlEquivalent);
        Assert.Contains(method.Executions, static execution => !execution.Equivalent);
    }

    private static RoundTripMethodRequirement Requirement() => new(
        "Tests.RoundTrip.ShotPolicy.ShouldFire",
        [
            new RoundTripExecutionCase(
                "below-threshold",
                [new ExecutionArgument("0.59"), new ExecutionArgument("true")]),
            new RoundTripExecutionCase(
                "at-threshold",
                [new ExecutionArgument("0.6"), new ExecutionArgument("true")]),
            new RoundTripExecutionCase(
                "disabled",
                [new ExecutionArgument("1.0"), new ExecutionArgument("false")]),
        ]);

    private static IdentitySidecarDocument InitializeIdentity(string source)
    {
        var request = new CSharpCompilationRequest(
            "RoundTripEquivalence",
            [new CSharpSourceDocument(
                "ShotPolicy.cs",
                source,
                IdentitySidecarDocument.CreateEmpty("ShotPolicy.cs"))]);
        CSharpAnalysisResult analysis = new CSharpSemanticPipeline().Analyze(request);
        Assert.True(analysis.Succeeded, string.Join(Environment.NewLine, analysis.Diagnostics));
        return Assert.Single(analysis.IdentitySidecars);
    }

    private static CSharpCompilationRequest Request(
        string source,
        IdentitySidecarDocument identity) => new(
            "RoundTripEquivalence",
            [new CSharpSourceDocument("ShotPolicy.cs", source, identity)]);
}
