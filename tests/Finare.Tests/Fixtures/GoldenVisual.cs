using Finare;

namespace Fixtures;

public static class GoldenVisual
{
    [Visual]
    public static int Add(int left, int right)
    {
        return left + right;
    }
}
