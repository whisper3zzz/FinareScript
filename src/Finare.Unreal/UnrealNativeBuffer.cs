namespace Finare.Unreal;

/// <summary>Generation-owned C++ contiguous storage. Its address is valid only inside a synchronous callback.</summary>
public sealed class UnrealNativeBuffer<T>(UnrealNativeObjectLease lease, ulong member, bool writable) where T : unmanaged
{
    public void WithSpan(UnrealSpanAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!writable) throw new InvalidOperationException("This native buffer is read-only.");
        lease.BorrowMemory(member, true, action);
    }
    public void ReadSpan(UnrealReadOnlySpanAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lease.BorrowMemory<T>(member, false, values => action(values));
    }
}
