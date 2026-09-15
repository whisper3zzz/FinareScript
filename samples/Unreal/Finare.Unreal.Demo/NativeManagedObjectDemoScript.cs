using Finare;
using Finare.Unreal;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Tests/ManagedObject", ClassId = "us_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
[UnrealScriptClass(
    UnrealScriptClassKind.Object,
    BaseClassPath = "/Script/Engine.PrimaryDataAsset",
    GeneratedClassPath = "/Game/FinareAutomation/BP_ManagedObject.BP_ManagedObject_C")]
public sealed partial class NativeManagedObjectDemoScript : UnrealObjectScript
{
    [Expose(InstanceEditable = true, SaveGame = true)]
    public partial int Value { get; set; } = 7;

    [Expose]
    public partial float WorldTime { get; private set; } = 0.0f;

    [Expose]
    public partial UnrealObject? Child { get; set; } = null;

    [Expose]
    public partial bool RejectStart { get; set; } = false;

    [Expose]
    public partial int RejectAfterGeneration { get; set; } = 0;

    [Visual]
    public int Add(int amount)
    {
        Value += amount;
        return Value;
    }

    protected override void OnInitialized()
    {
        if (RejectStart && Generation > RejectAfterGeneration)
            throw new InvalidOperationException("Rejected object start for test.");
        UnrealObject? target = UnrealObject.Wrap<UnrealObject>(TargetHandle, Bridge);
        if (HostHandle != TargetHandle)
            throw new InvalidOperationException("An Object script must receive its actual UObject as host and target.");
        var world = new FinareWorldLibrary(HostHandle, Bridge);
        WorldTime = world.GetWorldTimeSeconds(target);
        Child = world.CreateObject(target, world.LoadClassByPath(target,
            "/Script/FinareRuntime.FinareInteropFixture"), target)
            ?? throw new InvalidOperationException("The public object factory failed.");
        Value++;
    }

    protected override void OnUpdate(float deltaSeconds) =>
        throw new InvalidOperationException("Ordinary UObject behavior must not receive a synthetic Actor Tick.");
}
