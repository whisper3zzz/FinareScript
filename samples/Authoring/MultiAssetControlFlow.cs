using UnifiedScript;

namespace Samples.Authoring;

[ScriptAsset("Samples/CombatRules")]
public sealed class CombatRules
{
    public int Limit { get; set; } = 100;

    [Visual]
    public int Resolve(int input)
    {
        int scaled = input * 2;
        if (scaled > Limit)
        {
            scaled = Limit;
        }

        return scaled;
    }

    [Visual]
    public int Countdown(int value)
    {
        while (value > 0)
        {
            value = value - 1;
        }

        return value;
    }
}

[ScriptAsset("Samples/InventoryRules")]
public sealed class InventoryRules
{
    public int Total { get; set; } = 0;

    [Visual]
    public int Accumulate(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Total = Total + index;
        }

        return Total;
    }
}
