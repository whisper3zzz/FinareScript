using System;
using System.Collections.Generic;
using UnifiedScript;

namespace Samples.Execution;

public static class RuntimeRules
{
    [Visual]
    public static int LastNonNegative(IEnumerable<int> values, int seed)
    {
        int result = seed;
        foreach (int value in values)
        {
            if (value < 0)
            {
                continue;
            }

            result = value;
        }

        return result;
    }

    [Visual]
    public static int Absolute(int value)
    {
        return Math.Abs(value);
    }
}
