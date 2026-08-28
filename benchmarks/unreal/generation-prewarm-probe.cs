using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnifiedScript;
using UnifiedScript.Unreal;

namespace UnifiedScript.Benchmarks;

/// <summary>
/// Process-isolated first-Tick probe. The no-inline chain makes cold JIT work
/// observable without touching Unreal state or crossing the native bridge.
/// </summary>
[ScriptAsset("Benchmarks/GenerationPrewarmProbe")]
public sealed class GenerationPrewarmProbe : IUnrealManagedScript
{
    private long _checksum;

    public void OnStart(UnrealScriptContext context)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void OnTick(float deltaSeconds)
    {
        long seed = BitConverter.SingleToInt32Bits(deltaSeconds);
        Interlocked.Exchange(ref _checksum, Stage00(seed));
    }

    [Visual]
    public long VisualEntry(long value)
    {
        return value + 1;
    }

    public void OnStop()
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage00(long value) => Stage01(value + 3);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage01(long value) => Stage02(value ^ 0x11);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage02(long value) => Stage03(value * 5);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage03(long value) => Stage04(value - 7);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage04(long value) => Stage05(value ^ 0x23);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage05(long value) => Stage06(value + 11);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage06(long value) => Stage07(value * 13);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage07(long value) => Stage08(value - 17);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage08(long value) => Stage09(value ^ 0x35);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage09(long value) => Stage10(value + 19);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage10(long value) => Stage11(value * 23);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage11(long value) => Stage12(value - 29);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage12(long value) => Stage13(value ^ 0x47);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage13(long value) => Stage14(value + 31);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage14(long value) => Stage15(value * 37);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage15(long value) => Stage16(value - 41);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage16(long value) => Stage17(value ^ 0x59);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage17(long value) => Stage18(value + 43);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage18(long value) => Stage19(value * 47);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage19(long value) => Stage20(value - 53);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage20(long value) => Stage21(value ^ 0x61);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage21(long value) => Stage22(value + 59);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage22(long value) => Stage23(value * 61);
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Stage23(long value) => value - 67;
}
