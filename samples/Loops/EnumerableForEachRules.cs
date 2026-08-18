using System.Collections.Generic;
using UnifiedScript;

namespace Samples.Loops;

public static class EnumerableForEachRules
{
    [Visual]
    public static int Accumulate(IEnumerable<int> values, bool returnLarge)
    {
        int total = 0;
        foreach (int value in values)
        {
            if (value < 0)
            {
                continue;
            }

            if (returnLarge && value > 10)
            {
                return value;
            }

            total = total + value;
            if (total > 100)
            {
                break;
            }
        }

        return total;
    }
}
