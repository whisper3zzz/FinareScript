using UnifiedScript;
using UnifiedScript.Unreal.Generated;

namespace UnifiedScript.Unreal.Demo;

/// <summary>Generated reflected calls in both directions, including a retained native callback.</summary>
public sealed class CallbackGameplayDemoScript : IUnrealManagedScript
{
    private UnifiedScriptCallbackService? _service;
    private UnrealCallback<UnifiedScriptQueryCallbackHandler>? _callback;
    private int _calls;

    public void OnStart(UnrealScriptContext context)
    {
        _service = new(context.Target, context.Bridge);
        // A failed candidate reload restarts the retained generation. Reuse its
        // lease and restore the native slot that the rejected candidate replaced.
        _callback ??= _service.CreateStoreCallback(HandleQuery);
        BindCallback(_service, _callback);
        int before = _calls;
        string text = "start";
        if (!_service.EvaluateNow(_callback, 7, ref text, out var position) ||
            text != new string('x', 9000) + "start" || position != new UnrealVector(7, 2, 3) || _calls != before + 1)
            throw new InvalidOperationException("Generated immediate callback return/ref/out or exactly-once dispatch failed.");
        using (var temporary = _service.CreateEvaluateNowCallback(HandleQuery))
        {
            _service.StoreCallback(temporary);
            text = "temporary";
            if (!_service.Evaluate(7, ref text, out _)) throw new InvalidOperationException("Retained temporary callback failed.");
        }
        before = _calls;
        text = "released";
        if (_service.Evaluate(7, ref text, out _) || _calls != before || text != "released")
            throw new InvalidOperationException("A released callback still reached its managed handler.");
        _service.StoreCallback(_callback);
    }

    private bool HandleQuery(int value, ref string text, out UnrealVector position)
    {
        ++_calls;
        _service!.LastQueryValue = value;
        if (_service.LastQueryValue != value) throw new InvalidOperationException("Callback property reentry failed.");
        text = _service.FormatReply(text);
        position = new(value, 2, 3);
        return value == 7;
    }

    [Visual]
    public void BindCallback(UnifiedScriptCallbackService service, UnrealCallback<UnifiedScriptQueryCallbackHandler> callback)
    {
        service.StoreCallback(callback);
    }

    public bool EvaluateRetained(UnifiedScriptCallbackService service, int value)
    {
        string text = "tick";
        return service.Evaluate(value, ref text, out UnrealVector position);
    }

    public void OnTick(float deltaSeconds)
    {
        if (!EvaluateRetained(_service!, 7)) throw new InvalidOperationException("Retained callback failed after a generation transition.");
    }

    // Deliberately leave the retained lease to generation cleanup.
    public void OnStop() { }
}

public sealed class CallbackStartFailureDemoScript : IUnrealManagedScript
{
    private readonly CallbackGameplayDemoScript _inner = new();
    public void OnStart(UnrealScriptContext context)
    {
        _inner.OnStart(context);
        throw new InvalidOperationException("CallbackStartFailureDemoScript expected startup failure.");
    }
    public void OnTick(float deltaSeconds) { }
    public void OnStop() => _inner.OnStop();
}

/// <summary>Worker-originated forward calls execute their callbacks on the UE game thread.</summary>
public sealed class CallbackWorkerDemoScript : IUnrealManagedScript
{
    private UnifiedScriptCallbackService? _service;
    private Task? _workers;
    private int _calls;

    public void OnStart(UnrealScriptContext context)
    {
        _service = new(context.Target, context.Bridge);
        var callback = _service.CreateStoreCallback(HandleQuery);
        _service.StoreCallback(callback); // Generation owns the lease, including pending calls.
        _workers = Task.WhenAll(Task.Run(() => Query(7)), Task.Run(() => Query(8)));
    }

    private bool HandleQuery(int value, ref string text, out UnrealVector position)
    {
        ++_calls;
        _service!.LastQueryValue = value;
        if (_service.LastQueryValue != value) throw new InvalidOperationException("Worker callback property reentry failed.");
        // Nested variable-result call while another worker's outer result may be pending.
        text = _service.FormatReply(text);
        position = new(value, 2, 3);
        return value == 7;
    }

    private void Query(int value)
    {
        string text = "worker-" + value;
        bool result = _service!.Evaluate(value, ref text, out var position);
        if (result != (value == 7) || text != new string('x', 9000) + "worker-" + value ||
            position != new UnrealVector(value, 2, 3))
            throw new InvalidOperationException("Interleaved callback result/ref/out mismatch.");
    }

    public void OnTick(float deltaSeconds)
    {
        if (_workers?.IsCompleted != true) return; // Never synchronously wait on the game thread.
        _workers.GetAwaiter().GetResult();
        if (_calls != 2) throw new InvalidOperationException("An interleaved result retry reran its callback.");
        _service!.LastQueryValue = 200;
    }

    public void OnStop() { }
}
