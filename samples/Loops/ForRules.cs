using Finare;

namespace Samples.Loops;

public static class ForRules
{
    private static int state;

    [Visual]
    public static int SumSkippingTwo(int count)
    {
        int total = 0;
        for (int index = 0; index < count; index++)
        {
            if (index == 2)
            {
                continue;
            }

            total = total + index;
        }

        return total;
    }

    [Visual]
    public static int RunEffectfulPhases(int limit)
    {
        for (Reset(); state < limit; Advance())
        {
            if (state == 2)
            {
                continue;
            }

            if (state == 4)
            {
                break;
            }
        }

        return state;
    }

    private static void Reset()
    {
        state = 0;
    }

    private static void Advance()
    {
        state++;
    }
}
