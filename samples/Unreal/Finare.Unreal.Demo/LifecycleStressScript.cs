using System;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

/// <summary>
/// Long-lived latent workload used by the native deletion/travel/PIE/reload
/// stress matrix. Every generation owns one timer and one class-load request.
/// </summary>
public sealed class LifecycleStressScript : UnrealActorScript
{
    private FinareComponent? _host;

    protected override void OnBeginPlay()
    {
        if (!Context.HasDistinctTarget)
        {
            throw new InvalidOperationException(
                "The lifecycle stress script requires an Actor target.");
        }

        _host = new FinareComponent(HostHandle, Bridge);
        _host.ManagedState = $"StressStart:{Generation}";
        CancellationToken lifetime = Lifetime;
        _ = ObserveDelayAsync(Generation, lifetime);
        _ = ObserveClassLoadAsync(Generation, lifetime);
    }

    protected override void OnEndPlay()
    {
        if (!Lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The lifecycle scope must be canceled before the script stops.");
        }
        _host!.ManagedState = $"StressEnd:{Generation}";
    }

    [UnrealEvent(
        typeof(FinareComponent),
        "/Script/FinareRuntime.FinareComponent:OnManagedSignal",
        Source = UnrealEventSource.Host)]
    private void OnManagedSignal(int value, UnrealObjectHandle signalContext)
    {
        _host!.ManagedState = $"StressEvent:{Generation}:{value}";
    }

    private async Task ObserveDelayAsync(int generation, CancellationToken lifetime)
    {
        try
        {
            await DelayAsync(600.0f);
            if (!lifetime.IsCancellationRequested)
            {
                _host!.ManagedState = $"StaleDelay:{generation}";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveClassLoadAsync(int generation, CancellationToken lifetime)
    {
        try
        {
            UnrealObjectHandle loaded = await LoadClassAsync(
                "/Script/FinareRuntime.FinareComponent",
                timeoutSeconds: 5.0f,
                cancellationToken: lifetime);
            if (!lifetime.IsCancellationRequested)
            {
                _host!.ManagedState = loaded.IsWellFormed
                    ? $"StressLoad:{generation}"
                    : $"InvalidLoad:{generation}";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }
}
