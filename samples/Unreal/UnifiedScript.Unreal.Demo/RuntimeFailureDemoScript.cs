using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>Deterministic live-World failure used to verify bridge diagnostics.</summary>
public sealed class RuntimeFailureDemoScript : IUnrealManagedScript
{
    private UnrealScriptContext _context;

    public void OnStart(UnrealScriptContext context)
    {
        _context = context;
    }

    public void OnTick(float deltaSeconds)
    {
        UnrealObjectHandle staleTarget = UnrealObjectHandle.Create(
            _context.Target.SlotIndex,
            checked(_context.Target.Generation + 1));
        _ = new UnifiedScriptComponent(staleTarget, _context.Bridge).ManagedState;
    }

    public void OnStop()
    {
    }
}
