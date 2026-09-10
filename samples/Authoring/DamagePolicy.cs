using System;
using Finare;

namespace Game.Combat;

[ScriptAsset("Combat/DamagePolicy")]
public sealed class DamagePolicy
{
    [Expose(InstanceEditable = true, Category = "Damage")]
    public int BaseDamage { get; set; } = 20;

    [Expose(InstanceEditable = true, Category = "Damage")]
    public float CriticalMultiplier { get; set; } = 2.0f;

    [Expose(SaveGame = true)]
    public int MaxDamage { get; set; } = 999;

    [Visual]
    public float ApplyDamage(int incomingDamage, bool critical)
    {
        float scaled = incomingDamage * BaseDamage;
        if (critical)
        {
            scaled *= CriticalMultiplier;
        }

        scaled = Math.Clamp(scaled, 0f, MaxDamage);
        return scaled;
    }
}
