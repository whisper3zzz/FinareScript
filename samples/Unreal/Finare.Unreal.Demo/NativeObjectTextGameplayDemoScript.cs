using Finare;
using Finare.Unreal.Generated;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/NativeObjectTextGameplay")]
public sealed class NativeObjectTextGameplayDemoScript : IUnrealManagedScript
{
    private NativeCounter? _counter;

    [Expose(Category = "Native Object Text")]
    public string LastText { get; private set; } = "";

    [Expose(Category = "Native Object Text")]
    public int EmbeddedNulLength { get; private set; } = 0;

    [Visual]
    public string Echo(NativeCounter counter, string value)
    {
        return counter.EchoText(value);
    }

    public void OnStart(UnrealScriptContext context)
    {
        const string ConstructorPayload = "constructor héllo\0世界😀";
        const string FieldPayload = "updated héllo\0世界😀";
        if (_counter is null)
        {
            _counter = NativeCounter.Create(context.Bridge, ConstructorPayload);
            if (_counter.TextValue != ConstructorPayload || _counter.StdTextValue != ConstructorPayload)
            {
                throw new InvalidOperationException("Native text constructor values were not observable before field writes.");
            }
        }
        _counter.TextValue = FieldPayload;
        _counter.StdTextValue = FieldPayload;
        using NativeCounterChild child = _counter.GetChild();
        string mixed = _counter.MixText(child, FieldPayload, 2);
        if (_counter.EchoText(FieldPayload) != FieldPayload || _counter.EchoStdText(FieldPayload) != FieldPayload ||
            _counter.TextValue != FieldPayload || _counter.StdTextValue != FieldPayload ||
            mixed != FieldPayload + (child.Value + 2).ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Native UTF-8 text value transport did not preserve the declared string.");
        }
        LastText = _counter.EchoText(string.Empty);
        EmbeddedNulLength = FieldPayload.Length;
    }

    public void OnTick(float deltaSeconds)
    {
        if (deltaSeconds < 0.0f) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
    }

    public void OnStop() { }
}
