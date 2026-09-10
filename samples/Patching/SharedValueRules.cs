using Finare;

namespace Samples.Patching;

public static class SharedValueRules
{
    [Visual]
    public static float Combine(float damage, float bonus)
    {
        float sharedDamage = damage * 2.0f;
        return bonus + 1.0f;
    }
}
