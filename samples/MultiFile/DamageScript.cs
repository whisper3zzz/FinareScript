using Finare;

namespace Samples.MultiFile;

public static class DamageScript
{
    [Visual]
    public static float NormalizeDamage(float damage)
    {
        return GameplayApi.ClampToPositive(damage);
    }
}
