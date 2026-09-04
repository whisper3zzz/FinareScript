using UnifiedScript.Unreal;

namespace UnifiedScript.Tests;

public sealed class UnrealUmgBindingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SmallContractsNeverEmitDefaultOnlySwitches(bool property, bool method)
    {
        const string path = "/Script/Fixture.Small";
        var value = new UnrealTypeReference(UnrealValueKind.Int32, "int32");
        var manifest = UnrealReflectionManifest.Create(new(5, 8, 1, "test"), ["Fixture"],
        [new(path, "Small", "USmall", "Fixture", UnrealReflectedTypeKind.Class, null,
            new(false, true),
            property ? [new(path + ":Value", "Value", value, new(true, true, false))] : [],
            method ? [new(path + ":Read", "Read", value, [], new(false, true, true))] : [], "Small.h")]);
        string source = new UnrealNativeThunkGenerator().Generate(manifest).Source;
        Assert.DoesNotMatch(@"switch\s*\([^)]*\)\s*\{\s*default:", source);
        Assert.Contains("FailUnknownMember(OutResult, MemberId)", source);
    }

    [Fact]
    public void RealUmgContractGeneratesEventsNativeLookupAndByteEnumConversion()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "UnrealFixtures");
        var reflection = UnrealReflectionJson.Deserialize(File.ReadAllText(Path.Combine(root, "UMG.reflection.json")));
        var contract = UnrealApiContractJson.Deserialize(File.ReadAllText(Path.Combine(root, "UMG.api-contract.json")));
        var projection = UnrealApiContractProjector.Project(reflection, contract);
        var managed = new UnrealBindingGenerator().Generate(projection.Reflection,
            new UnrealBindingGeneratorOptions(contract.ManagedNamespace));
        var native = new UnrealNativeThunkGenerator().Generate(projection.NativeReflection,
            new UnrealNativeThunkGeneratorOptions(contract.NativeHeader, contract.NativeNamespace));
        Assert.Equal(File.ReadAllText(Path.Combine(root, "UMG.bindings.cs")), managed.Source);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "UMG.native-thunks.generated.cpp")), native.Source);
        var coverage = UnrealBindingCoverageReport.Create(projection, managed, native);
        Assert.True(coverage.Gate.Passed);
        Assert.DoesNotContain(coverage.Entries, item => item.Disposition == UnrealBindingDisposition.Rejected);
        Assert.Contains("SubscribeOnClicked(OnClickedHandler handler)", managed.Source);
        Assert.Contains("GetWidgetFromName", managed.Source);
        Assert.Contains("static_cast<UWidget* (UUserWidget::*)(FName const&) const>", native.Source);
        Assert.Contains("TEnumAsByte<EComponentMobility::Type> Argument0 = static_cast<EComponentMobility::Type>(Argument0Raw)", native.Source);
        Assert.Contains("Argument0Raw < 0 || Argument0Raw > 255", native.Source);
        Assert.DoesNotMatch(@"switch\s*\([^)]*\)\s*\{\s*default:", native.Source);
        var nodes = UnrealApiNodeCatalogGenerator.Generate(projection);
        Assert.Contains(nodes.Types.SelectMany(type => type.Nodes), node => node.MemberPath.Contains("GetWidgetFromName"));
        Assert.Contains(nodes.Types.SelectMany(type => type.Nodes), node => node.MemberPath == "/Script/UMG.Button:OnClicked");
    }
}
