using UnifiedScript;

namespace UnifiedScript.Unreal.Diagnostics;

public static class GameplayDiagnosisFixture
{
    [Visual]
    public static int Calculate(int baseDamage, bool slowTrace)
    {
        int damage = 0;
        int traceBudget = slowTrace ? 5 : 1;
        return damage + traceBudget - traceBudget;
    }
}
