using System;

namespace Samples.MultiFile;

public static class GameplayApi
{
    public static float ClampToPositive(float value) => MathF.Max(value, 0.0f);
}
