using Finare.Unreal;
using Finare.ApiDiscovery.Generated;

namespace Finare.ApiDiscovery;

public sealed class DiscoveryScript : IUnrealManagedScript
{
    public void OnStart(UnrealScriptContext context)
    {
        var component = new FinareApiDiscoveryComponent(context.Host, context.Bridge);
        component.DiscoveryResult = component.DiscoveryAdd(20, 22);
    }

    public void OnTick(float deltaSeconds) { }
    public void OnStop() { }
}
