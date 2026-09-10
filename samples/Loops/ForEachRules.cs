using Finare;

namespace Samples.Loops;

public static class ForEachRules
{
    [Visual]
    public static int SumPositive(int[] values)
    {
        int total = 0;
        foreach (int value in values)
        {
            if (value <= 0)
            {
                continue;
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
