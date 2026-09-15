using Finare.Unreal;
using System.Runtime.InteropServices;

namespace Finare.Tests;

public sealed partial class UnrealConstructionTests
{
    private static readonly UnrealObjectHandle Host = UnrealObjectHandle.Create(0, 7);

    [Fact]
    public void ConstructorUsesNativePropertiesWithoutStartingGameplayAndExpiresViews()
    {
        var bridge = new FakeBridge();
        var context = new UnrealConstructionContext(bridge, Host, true, (_, _, _, _, _) => Host);
        var script = new Script();
        script.RunConstruction(context);
        Assert.False(script.IsRunning);
        Assert.False(script.Began);
        Assert.Equal(37, bridge.Value);
        Assert.Throws<ObjectDisposedException>(() => script.Saved!.Host);
        Assert.Throws<ObjectDisposedException>(() => script.View!.Handle);
        Assert.Throws<ObjectDisposedException>(() => script.Seed);
        Assert.Throws<InvalidOperationException>(() => script.Lifetime);
    }

    [Fact]
    public void ConstructorFailureStillExpiresContext()
    {
        var context = new UnrealConstructionContext(new FakeBridge(), Host, false,
            (_, _, _, _, _) => throw new InvalidOperationException("native failure"));
        var script = new FailingScript();
        Assert.Throws<InvalidOperationException>(() => script.RunConstruction(context));
        Assert.Throws<ObjectDisposedException>(() => context.Host);
        Assert.False(script.IsRunning);
    }

    [Fact]
    public async Task ConstructorRejectsAccessFromAnotherThreadBeforeNativeCall()
    {
        using var context = new UnrealConstructionContext(new FakeBridge(), Host, false, (_, _, _, _, _) => Host);
        var failure = await Task.Run(() => Record.Exception(() => context.Host));
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void ConstructionOperationsAbiMatchesNative()
    {
        Assert.Equal(16, Marshal.SizeOf<UnrealWorldManagedEntryPoint.NativeConstructionOperations>());
        Assert.Equal(8, Marshal.OffsetOf<UnrealWorldManagedEntryPoint.NativeConstructionOperations>("Execute").ToInt32());
    }

    [Fact]
    public void AsyncVoidHookIsRejectedBeforeItStarts()
    {
        using var context = new UnrealConstructionContext(new FakeBridge(), Host, false, (_, _, _, _, _) => Host);
        var script = new AsyncScript();
        Assert.Throws<InvalidOperationException>(() => script.RunConstruction(context));
        Assert.False(script.Entered);
        Assert.Throws<ObjectDisposedException>(() => context.Host);
    }

    [ScriptAsset("Tests/Construction", ClassId = "us_bfd54612a1bd4f51962f93be6912c704")]
    [UnrealScriptClass(UnrealScriptClassKind.Actor, GeneratedClassPath = "/Game/Tests/Construct.Construct_C")]
    private sealed partial class Script : UnrealActorScript
    {
        [Expose] public partial int Seed { get; set; }
        public UnrealConstructionContext? Saved;
        public UnrealObject? View;
        public bool Began;
        protected override void OnConstruct(UnrealConstructionContext context)
        { Saved = context; View = context.Host; Seed = 37; Assert.True(context.IsClassDefaultObject); }
        protected override void OnBeginPlay() => Began = true;
    }
    private sealed class FailingScript : UnrealActorScript
    {
        protected override void OnConstruct(UnrealConstructionContext context) => context.CreateDefaultSubobject<UnrealObject>("Fails");
    }
    private sealed class AsyncScript : UnrealActorScript
    {
        public bool Entered;
        protected override async void OnConstruct(UnrealConstructionContext context)
        { Entered = true; await Task.Yield(); }
    }
    private sealed class FakeBridge : IUnrealInvocationBridge, IUnrealObjectTypeBridge
    {
        public int Value;
        public T ReadProperty<T>(UnrealObjectHandle target, string path) => (T)(object)Value;
        public void WriteProperty<T>(UnrealObjectHandle target, string path, T value) => Value = (int)(object)value!;
        public T Invoke<T>(UnrealObjectHandle target, string path, IReadOnlyList<object?> args) => throw new NotSupportedException();
        public void Invoke(UnrealObjectHandle target, string path, IReadOnlyList<object?> args) => throw new NotSupportedException();
        public IReadOnlyList<string> GetObjectTypePaths(UnrealObjectHandle target) => ["/Script/CoreUObject.Object"];
        public bool IsObjectOfClass(UnrealObjectHandle target, string path) => true;
    }
}
