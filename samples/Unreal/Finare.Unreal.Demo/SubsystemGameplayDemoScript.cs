using System;
using Finare;
using Finare.Unreal;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/SubsystemGameplay")]
public sealed class SubsystemGameplayDemoScript : IUnrealManagedScript
{
    private IUnrealInvocationBridge? _bridge;
    private FinareInteropFixture? _fixture;
    private SubsystemBlueprintLibrary? _subsystems;
    private FinareWorldLibrary? _worldLibrary;
    private EnhancedInputSubsystemInterface? _input;
    private UnrealObjectHandle _mappingContext;
    private ModifyContextOptions _mappingOptions;
    private bool _mappingInstalled;

    [Expose(Category = "Subsystems")]
    public bool InputMappingWasRemoved { get; private set; } = false;

    [Expose(Category = "Subsystems")]
    public bool InputMappingActive { get; private set; } = false;

    [Expose(Category = "Subsystems")]
    public int MappingPriority { get; private set; } = -1;

    [Expose(Category = "Subsystems")]
    public bool ReplayPlaying { get; private set; } = false;

    [Expose(Category = "Subsystems")]
    public bool ReplayRecording { get; private set; } = false;

    [Expose(Category = "Subsystems")]
    public bool WorldStreamingCompleted { get; private set; } = false;

    [Visual]
    public bool QueryMapping(UnrealObjectHandle subsystem, UnrealObjectHandle mappingContext)
    {
        if (_bridge == null)
        {
            return false;
        }
        return QueryMappingCore(subsystem, mappingContext);
    }

    private bool QueryMappingCore(UnrealObjectHandle subsystem, UnrealObjectHandle mappingContext)
    {
        var input = new EnhancedInputSubsystemInterface(subsystem, _bridge!);
        int priority;
        return input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, _bridge!), out priority);
    }

    public void OnStart(UnrealScriptContext context)
    {
        _bridge = context.Bridge;
        _fixture = new FinareInteropFixture(context.Host, context.Bridge);
        _subsystems = new SubsystemBlueprintLibrary(context.Target, context.Bridge);
        _worldLibrary = new FinareWorldLibrary(context.Target, context.Bridge);

        UnrealClassHandle worldSubsystemClass = _worldLibrary.LoadClassByPath(
            UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
            "/Script/Engine.WorldPartitionSubsystem");
        UnrealObjectHandle worldSubsystem = (_subsystems.GetWorldSubsystem(
            UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
            worldSubsystemClass))?.Handle ?? default;
        if (worldSubsystem.IsNull)
        {
            throw new InvalidOperationException("The live world did not provide a World Partition subsystem.");
        }
        WorldStreamingCompleted = new WorldPartitionSubsystem(worldSubsystem, context.Bridge)
            .IsAllStreamingCompleted();

        UnrealClassHandle replayClass = _worldLibrary.LoadClassByPath(
            UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
            "/Script/Engine.ReplaySubsystem");
        UnrealObjectHandle replaySubsystem = (_subsystems.GetGameInstanceSubsystem(
            UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
            replayClass))?.Handle ?? default;
        if (replaySubsystem.IsNull)
        {
            throw new InvalidOperationException("The live game instance did not provide a Replay subsystem.");
        }
        var replay = new ReplaySubsystem(replaySubsystem, context.Bridge);
        ReplayPlaying = replay.IsPlaying();
        ReplayRecording = replay.IsRecording();
        _ = replay.GetActiveReplayName();

        UnrealClassHandle enhancedInputSubsystemClass = _worldLibrary.LoadClassByPath(
            UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
            "/Script/EnhancedInput.EnhancedInputLocalPlayerSubsystem");
        UnrealObjectHandle enhancedInputSubsystem = (_subsystems
            .GetLocalPlayerSubSystemFromPlayerController(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge), enhancedInputSubsystemClass))?.Handle ?? default;
        if (enhancedInputSubsystem.IsNull)
        {
            enhancedInputSubsystem = (_subsystems.GetLocalPlayerSubsystem(
                UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(context.Target, context.Bridge),
                enhancedInputSubsystemClass))?.Handle ?? default;
        }

        UnrealObjectHandle mappingContext = (_fixture.ObjectValue)?.Handle ?? default;
        if (enhancedInputSubsystem.IsNull || mappingContext.IsNull)
        {
            throw new InvalidOperationException(
                "The live PlayerController did not provide an Enhanced Input subsystem and mapping context.");
        }

        var input = new EnhancedInputSubsystemInterface(enhancedInputSubsystem, context.Bridge);
        var options = new ModifyContextOptions(
            bForceImmediately: true,
            bIgnoreAllPressedKeysUntilRelease: true,
            bNotifyUserSettings: false);
        if (input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), out int existingPriority))
        {
            throw new InvalidOperationException(
                $"The mapping context was already registered with priority {existingPriority}; refusing to overwrite it.");
        }
        input.AddMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), 37, options);
        _input = input;
        _mappingContext = mappingContext;
        _mappingOptions = options;
        _mappingInstalled = true;
        InputMappingActive = true;
        if (!input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), out int priority) || priority != 37)
        {
            throw new InvalidOperationException("Generated Enhanced Input HasMappingContext did not return priority 37.");
        }

        MappingPriority = priority;
        input.RemoveMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), options);
        InputMappingWasRemoved = !input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), out _);
        if (!InputMappingWasRemoved)
        {
            throw new InvalidOperationException("Generated Enhanced Input RemoveMappingContext did not remove the context.");
        }

        input.AddMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), 37, options);
        if (!input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(mappingContext, context.Bridge), out priority) || priority != 37)
        {
            throw new InvalidOperationException("Generated Enhanced Input re-add did not restore priority 37.");
        }
        InputMappingWasRemoved = false;
        InputMappingActive = true;

        _fixture.Int32Value = MappingPriority;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
    }

    public void OnStop()
    {
        if (_mappingInstalled && _input is not null && !_mappingContext.IsNull)
        {
            _input.RemoveMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(_mappingContext, _bridge!), _mappingOptions);
            if (_input.HasMappingContext(UnrealObject.Wrap<global::Finare.Unreal.UnrealObject>(_mappingContext, _bridge!), out _))
            {
                throw new InvalidOperationException("Generated Enhanced Input cleanup left the mapping context registered.");
            }
            _mappingInstalled = false;
            InputMappingActive = false;
            InputMappingWasRemoved = true;
        }
        _input = null;
        _fixture = null;
        _subsystems = null;
        _worldLibrary = null;
        _bridge = null;
    }
}
