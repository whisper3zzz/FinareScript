namespace Finare.Unreal;

/// <summary>
/// Binary call-boundary value. Borrow retains a managed array without copying;
/// crossing the native boundary always copies bytes. Returned values own snapshots.
/// </summary>
[UnrealValue]
public readonly struct UnrealByteBuffer
{
    public const int MaximumLength = 16 * 1024 * 1024;
    public const int MaximumCallBytes = 64 * 1024 * 1024;
    private readonly ReadOnlyMemory<byte> _bytes;
    private UnrealByteBuffer(ReadOnlyMemory<byte> bytes, bool borrowed)
    {
        if (bytes.Length > MaximumLength) throw new ArgumentOutOfRangeException(nameof(bytes));
        _bytes = bytes;
        IsBorrowed = borrowed;
    }
    public int Length => _bytes.Length;
    public bool IsBorrowed { get; }
    public ReadOnlySpan<byte> Span => _bytes.Span;
    public byte this[int index] => _bytes.Span[index];
    public static UnrealByteBuffer CopyFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumLength) throw new ArgumentOutOfRangeException(nameof(bytes));
        return new(bytes.ToArray(), false);
    }
    public static UnrealByteBuffer Borrow(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new(bytes, true);
    }
    public UnrealByteBuffer Slice(int start, int length) => new(_bytes.Slice(start, length), IsBorrowed);
    public byte[] ToArray() => _bytes.ToArray();
}
