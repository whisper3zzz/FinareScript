using System;
using UnifiedScript;
using UnifiedScript.Unreal;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

[ScriptAsset("Unreal/SubsystemGameplay")]
public sealed class SubsystemGameplayDemoScript : IUnrealManagedScript
{
    private IUnrealInvocationBridge? _bridge;
    private UnifiedScriptInteropFixture? _fixture;
    private SubsystemBlueprintLibrary? _subsystems;
    private UnifiedScriptWorldLibrary? _worldLibrary;
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
        return input.HasMappingContext(mappingContext, out priority);
    }

    public void OnStart(UnrealScriptContext context)
    {
        _bridge = context.Bridge;
        _fixture = new UnifiedScriptInteropFixture(context.Host, context.Bridge);
        _subsystems = new SubsystemBlueprintLibrary(context.Target, context.Bridge);
        _worldLibrary = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);

        UnrealClassHandle worldSubsystemClass = _worldLibrary.LoadClassByPath(
            context.Target,
            "/Script/Engine.WorldPartitionSubsystem");
        UnrealObjectHandle worldSubsystem = _subsystems.GetWorldSubsystem(
            context.Target,
            worldSubsystemClass);
        if (worldSubsystem.IsNull)
        {
            throw new InvalidOperationException("The live world did not provide a World Partition subsystem.");
        }
        WorldStreamingCompleted = new WorldPartitionSubsystem(worldSubsystem, context.Bridge)
            .IsAllStreamingCompleted();

        UnrealClassHandle replayClass = _worldLibrary.LoadClassByPath(
            context.Target,
            "/Script/Engine.ReplaySubsystem");
        UnrealObjectHandle replaySubsystem = _subsystems.GetGameInstanceSubsystem(
            context.Target,
            replayClass);
        if (replaySubsystem.IsNull)
        {
            throw new InvalidOperationException("The live game instance did not provide a Replay subsystem.");
        }
        var replay = new ReplaySubsystem(replaySubsystem, context.Bridge);
        ReplayPlaying = replay.IsPlaying();
        ReplayRecording = replay.IsRecording();
        _ = replay.GetActiveReplayName();

        UnrealClassHandle enhancedInputSubsystemClass = _worldLibrary.LoadClassByPath(
            context.Target,
            "/Script/EnhancedInput.EnhancedInputLocalPlayerSubsystem");
        UnrealObjectHandle enhancedInputSubsystem = _subsystems
            .GetLocalPlayerSubSystemFromPlayerController(context.Target, enhancedInputSubsystemClass);
        if (enhancedInputSubsystem.IsNull)
        {
            enhancedInputSubsystem = _subsystems.GetLocalPlayerSubsystem(
                context.Target,
                enhancedInputSubsystemClass);
        }

        UnrealObjectHandle mappingContext = _fixture.ObjectValue;
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
        if (input.HasMappingContext(mappingContext, out int existingPriority))
        {
            throw new InvalidOperationException(
                $"The mapping context was already registered with priority {existingPriority}; refusing to overwrite it.");
        }
        input.AddMappingContext(mappingContext, 37, options);
        _input = input;
        _mappingContext = mappingContext;
        _mappingOptions = options;
        _mappingInstalled = true;
        InputMappingActive = true;
        if (!input.HasMappingContext(mappingContext, out int priority) || priority != 37)
        {
            throw new InvalidOperationException("Generated Enhanced Input HasMappingContext did not return priority 37.");
        }

        MappingPriority = priority;
        input.RemoveMappingContext(mappingContext, options);
        InputMappingWasRemoved = !input.HasMappingContext(mappingContext, out _);
        if (!InputMappingWasRemoved)
        {
            throw new InvalidOperationException("Generated Enhanced Input RemoveMappingContext did not remove the context.");
        }

        input.AddMappingContext(mappingContext, 37, options);
        if (!input.HasMappingContext(mappingContext, out priority) || priority != 37)
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
            _input.RemoveMappingContext(_mappingContext, _mappingOptions);
            if (_input.HasMappingContext(_mappingContext, out _))
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
