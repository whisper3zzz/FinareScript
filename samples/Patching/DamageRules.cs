using Finare;

namespace Samples.Patching;

public static class DamageRules
{
    [Visual]
    public static float Scale(float damage)
    {
        return damage * 1.0f;
    }
}
