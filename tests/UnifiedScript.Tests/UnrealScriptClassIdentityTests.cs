using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealScriptClassIdentityTests
{
    private const string StableId = "us_0123456789abcdef0123456789abcdef";
    private const string GeneratedPath = "/Game/Tests/Active.Active_C";

    [Fact]
    public void ExplicitClassIdUsesStableLocatorForInheritedProjection()
    {
        string path = UnrealScriptClassIdentity.MemberPath(
            typeof(StableActor),
            GeneratedPath,
            "Seed");

        Assert.Equal(
            "unifiedscript-class/us_0123456789abcdef0123456789abcdef:Seed",
            path);
    }

    [Fact]
    public void MissingClassIdRetainsLegacyGeneratedClassPath()
    {
        Assert.Equal(
            $"{GeneratedPath}:Seed",
            UnrealScriptClassIdentity.MemberPath(typeof(LegacyActor), GeneratedPath, "Seed"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("us_00000000000000000000000000000000")]
    [InlineData("us_0123456789ABCDEF0123456789abcdef")]
    [InlineData("class-id")]
    [InlineData("")]
    [InlineData(" ")]
    public void MalformedOrZeroClassIdIsRejected(string classId)
    {
        Type type = classId == StableId
            ? typeof(StableActor)
            : classId == "0123456789abcdef0123456789abcdef"
                ? typeof(MissingPrefixActor)
                : classId == "us_00000000000000000000000000000000"
                    ? typeof(ZeroActor)
                    : classId == "us_0123456789ABCDEF0123456789abcdef"
                        ? typeof(UppercaseActor)
                        : classId == "class-id"
                            ? typeof(MalformedActor)
                            : classId.Length == 0
                                ? typeof(EmptyClassIdActor)
                                : typeof(WhitespaceClassIdActor);

        Assert.Throws<InvalidOperationException>(() =>
            UnrealScriptClassIdentity.MemberPath(type, GeneratedPath, "Seed"));
    }

    [Fact]
    public void ParserRequiresNonZeroLowercaseIdentity()
    {
        Assert.True(UnrealScriptClassIdentity.TryParseClassId(StableId, out string digits));
        Assert.Equal(StableId[3..], digits);
        Assert.False(UnrealScriptClassIdentity.TryParseClassId("us_00000000000000000000000000000000", out _));
        Assert.False(UnrealScriptClassIdentity.TryParseClassId("us_0123456789ABCDEF0123456789abcdef", out _));
    }

    [ScriptAsset("Game/Stable", ClassId = StableId)]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class StableActor : UnrealActorScript
    {
        [Expose] public int Seed { get; set; }
    }

    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class LegacyActor : UnrealActorScript
    {
        [Expose] public int Seed { get; set; }
    }

    [ScriptAsset("Game/MissingPrefix", ClassId = "0123456789abcdef0123456789abcdef")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class MissingPrefixActor : UnrealActorScript { }

    [ScriptAsset("Game/Zero", ClassId = "us_00000000000000000000000000000000")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class ZeroActor : UnrealActorScript { }

    [ScriptAsset("Game/Uppercase", ClassId = "us_0123456789ABCDEF0123456789abcdef")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class UppercaseActor : UnrealActorScript { }

    [ScriptAsset("Game/Malformed", ClassId = "class-id")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class MalformedActor : UnrealActorScript { }

    [ScriptAsset("Game/Empty", ClassId = "")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class EmptyClassIdActor : UnrealActorScript { }

    [ScriptAsset("Game/Whitespace", ClassId = " ")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = GeneratedPath)]
    private sealed class WhitespaceClassIdActor : UnrealActorScript { }
}
