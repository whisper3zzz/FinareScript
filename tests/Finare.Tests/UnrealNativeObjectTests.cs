using System.Runtime.InteropServices;
using Finare.Unreal;

namespace Finare.Tests;

[Trait("Category", "Unreal")]
public sealed class UnrealNativeObjectTests
{
    [Fact]
    public void OwnershipAbiHasIndependentFixedLayout()
    {
        Assert.Equal(64, Marshal.SizeOf<UnrealNativeObjectTransportTableV1>());
        Assert.Equal(72, Marshal.SizeOf<UnrealNativeObjectTransportTableWithCapabilities>());
        Assert.Equal(80, Marshal.SizeOf<UnrealNativeObjectTransportTableWithText>());
        Assert.Equal(56, Marshal.SizeOf<UnrealNativeObjectTextTransportTableV1>());
        Assert.Equal(64, Marshal.OffsetOf<UnrealNativeObjectTransportTableWithCapabilities>(nameof(UnrealNativeObjectTransportTableWithCapabilities.Capabilities)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<UnrealNativeObjectTransportTableV1>(nameof(UnrealNativeObjectTransportTableV1.Invoke)).ToInt32());
        Assert.Equal(48, Marshal.SizeOf<UnrealNativeValueV3>());
        Assert.Equal(72, Marshal.SizeOf<UnrealNativeCallFrameV3>());
    }

    [Fact]
    public void GenerationScopesAreLazyIndependentAndCloseOnDispose()
    {
        var transport = new FixtureTransport();
        var world = new NativeUnrealInvocationBridge(transport);
        object first = Generation(world), second = Generation(world);
        Assert.Empty(transport.Scopes);
        var firstScope = ((IUnrealNativeObjectProvider)first).NativeObjects;
        Assert.Same(firstScope, ((IUnrealNativeObjectProvider)first).NativeObjects);
        var secondScope = ((IUnrealNativeObjectProvider)second).NativeObjects;
        var worldScope = world.NativeObjects;
        Assert.Equal(3, transport.Scopes.Count);
        var oldLease = firstScope.Construct(1, []);
        var nextLease = secondScope.Construct(1, []);
        ((IDisposable)first).Dispose();
        Assert.Equal(2, transport.Scopes.Count);
        Assert.Throws<ObjectDisposedException>(() => oldLease.Invoke(2, []));
        Assert.Throws<ObjectDisposedException>(() => ((IUnrealNativeObjectProvider)first).NativeObjects);
        nextLease.Invoke(2, []);
        ((IDisposable)second).Dispose();
        worldScope.Dispose();
        Assert.Empty(transport.Scopes);
        oldLease.Dispose();
    }

    [Fact]
    public void OwnedLeaseExplicitDisposalIsIdempotentAndCannotInvokeAgain()
    {
        var transport = new FixtureTransport();
        using var scope = new UnrealNativeObjectScope(transport);
        var lease = scope.Construct(1, []);
        lease.Invoke(2, []);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, transport.Releases);
        Assert.Throws<ObjectDisposedException>(() => lease.Invoke(2, []));
        Assert.Throws<ObjectDisposedException>(() => lease.Borrow(3, []));
    }

    [Theory]
    [InlineData(UnrealNativeObjectStatus.InvalidHandle)]
    [InlineData(UnrealNativeObjectStatus.Unavailable)]
    [InlineData(UnrealNativeObjectStatus.InvalidScope)]
    public void DisposeAcceptsAlreadyInvalidatedOwnership(UnrealNativeObjectStatus status)
    {
        var transport = new FixtureTransport();
        using var scope = new UnrealNativeObjectScope(transport);
        var lease = scope.Construct(1, []);
        transport.ReleaseStatus = status;
        lease.Dispose();
    }

    [Fact]
    public void WorkerDisposalFailureRemainsRetryableForLeaseScopeAndGeneration()
    {
        var transport = new FixtureTransport();
        var generation = Generation(new NativeUnrealInvocationBridge(transport));
        var scope = ((IUnrealNativeObjectProvider)generation).NativeObjects;
        var lease = scope.Construct(1, []);
        transport.ReleaseStatus = UnrealNativeObjectStatus.WrongThread;
        Assert.Equal(UnrealNativeObjectStatus.WrongThread,
            Assert.Throws<UnrealNativeObjectException>(() => lease.Dispose()).Status);
        lease.Invoke(2, []);
        transport.CloseStatus = UnrealNativeObjectStatus.WrongThread;
        Assert.Throws<UnrealNativeObjectException>(() => ((IDisposable)generation).Dispose());
        Assert.Same(scope, ((IUnrealNativeObjectProvider)generation).NativeObjects);
        transport.ReleaseStatus = transport.CloseStatus = UnrealNativeObjectStatus.Success;
        lease.Dispose();
        ((IDisposable)generation).Dispose();
        Assert.Empty(transport.Scopes);
    }

    [Fact]
    public void UnsupportedTransportCannotSilentlyUseReflectedCalls()
    {
        var transport = new FixtureTransport { SupportsNativeObjects = false };
        var bridge = new NativeUnrealInvocationBridge(transport);
        Assert.Throws<NotSupportedException>(() => UnrealNativeObjectScope.FromBridge(bridge));
        Assert.Empty(transport.Scopes);
    }

    [Fact]
    public void ObjectArgumentsRequireTheExactLiveScopeAndExplicitNullability()
    {
        var transport = new FixtureTransport();
        using var scope = new UnrealNativeObjectScope(transport);
        using var otherScope = new UnrealNativeObjectScope(transport);
        using var value = new Wrapped(scope.Construct(1, []));
        var cell = scope.EncodeObject(value, 42);
        Assert.Equal(UnrealNativeValueKindV3.NativeObject, cell.Kind);
        Assert.Equal(42UL, cell.TypeId);
        Assert.Equal(value.Handle.Value, cell.Payload0);
        Assert.Equal(UnrealNativeValueFlagsV3.None, cell.Flags);
        Assert.Throws<ArgumentNullException>(() => scope.EncodeObject(null, 42));
        Assert.Throws<ArgumentOutOfRangeException>(() => scope.EncodeObject(value, 0));
        var empty = scope.EncodeObject(null, 42, allowNull: true);
        Assert.Equal(UnrealNativeValueFlagsV3.Null, empty.Flags);
        Assert.Equal(0UL, empty.Payload0);
        Assert.Equal(UnrealNativeObjectStatus.WrongOwner,
            Assert.Throws<UnrealNativeObjectException>(() => otherScope.EncodeObject(value, 42)).Status);
        value.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scope.EncodeObject(value, 42));
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scope.EncodeObject(null, 42, true));
    }

    [Fact]
    public void OldTransportKeepsFixedCallsButRejectsObjectArguments()
    {
        var transport = new FixtureTransport { SupportsNativeObjectArguments = false };
        using var scope = new UnrealNativeObjectScope(transport);
        using var value = new Wrapped(scope.Construct(1, []));
        Assert.Throws<NotSupportedException>(() => scope.EncodeObject(value, 42));
        Assert.Throws<NotSupportedException>(() => scope.EncodeObject(null, 42, true));
    }

    [Fact]
    public void TextCodecPreservesUnicodeNulAndRejectsInvalidDescriptors()
    {
        var arena = new List<byte>();
        string expected = "héllo\0世界😀";
        var cell = UnrealNativeObjectValues.EncodeText(expected, arena);
        Assert.Equal(expected, UnrealNativeObjectValues.DecodeText(cell, CollectionsMarshal.AsSpan(arena)));
        Assert.Throws<ArgumentNullException>(() => UnrealNativeObjectValues.EncodeText(null!, arena));
        var invalidRange = cell;
        invalidRange.Payload1 = (ulong)arena.Count + 1;
        Assert.Throws<InvalidOperationException>(() => UnrealNativeObjectValues.DecodeText(invalidRange, CollectionsMarshal.AsSpan(arena)));
        var invalidSpare = cell;
        invalidSpare.Payload2 = 1;
        Assert.Throws<InvalidOperationException>(() => UnrealNativeObjectValues.DecodeText(invalidSpare, CollectionsMarshal.AsSpan(arena)));
        invalidSpare = cell;
        invalidSpare.Payload3 = 1;
        Assert.Throws<InvalidOperationException>(() => UnrealNativeObjectValues.DecodeText(invalidSpare, CollectionsMarshal.AsSpan(arena)));
    }

    [Fact]
    public void TextPendingReadPreservesTokenUntilSuccessfulCollection()
    {
        var transport = new FixtureTransport();
        using var scope = new UnrealNativeObjectScope(transport);
        using var pending = scope.BeginText(default, 99, [], []);
        Assert.Equal(3u, pending.RequiredBytes);
        var tooSmall = new byte[1];
        Assert.Equal(UnrealNativeObjectStatus.TextBufferTooSmall,
            Assert.Throws<UnrealNativeObjectException>(() => pending.Read(tooSmall)).Status);
        Assert.Equal("abc", pending.Read().DecodeString());
        Assert.Throws<ObjectDisposedException>(() => pending.Read());
    }

    private sealed class Wrapped(UnrealNativeObjectLease lease) : UnrealNativeObject(lease);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeObjectCellsAreNotReflectedObjectsOrFixedValues(bool isNull)
    {
        var cell = new UnrealNativeValueV3
        {
            Kind = UnrealNativeValueKindV3.NativeObject, TypeId = 42,
            Payload0 = isNull ? 0UL : 123UL,
            Flags = isNull ? UnrealNativeValueFlagsV3.Null : UnrealNativeValueFlagsV3.None,
        };
        Assert.False(UnrealNativeValueCodecV3.TryDecode<UnrealObjectHandle>(cell, [], 0, out _));
        Assert.False(UnrealNativeValueCodecV3.TryDecode<UnrealClassHandle>(cell, [], 0, out _));
        Assert.False(UnrealNativeValueCodecV3.TryDecodeObject(typeof(UnrealObjectHandle), cell, [], 0, out _));
        Assert.Throws<InvalidOperationException>(() => UnrealNativeObjectValues.Decode<UnrealObjectHandle>(cell));
        Assert.Throws<InvalidOperationException>(() => UnrealNativeObjectValues.Decode<ulong>(cell));
    }

    private static object Generation(NativeUnrealInvocationBridge world) =>
        Activator.CreateInstance(typeof(UnrealObject).Assembly.GetType(
            "Finare.Unreal.UnrealGenerationInvocationBridge", throwOnError: true)!, world)!;

    private sealed class FixtureTransport : IUnrealNativeObjectTransport, IUnrealNativeInvocationTransport
    {
        private ulong _next;
        public HashSet<ulong> Scopes { get; } = [];
        public int Releases { get; private set; }
        public bool SupportsNativeObjects { get; init; } = true;
        public bool SupportsNativeObjectArguments { get; init; } = true;
        public bool SupportsNativeObjectText => true;
        public uint NativeObjectTextMaxInputBytes => UnrealNativeObjectValues.MaximumTextArenaBytes;
        public uint NativeObjectTextMaxOutputBytes => UnrealNativeObjectValues.MaximumTextArenaBytes;
        private readonly HashSet<ulong> _pendingText = [];
        public UnrealNativeObjectStatus ReleaseStatus { get; set; }
        public UnrealNativeObjectStatus CloseStatus { get; set; }
        public UnrealNativeObjectStatus OpenNativeObjectScope(out ulong scope)
        {
            Scopes.Add(scope = ++_next);
            return UnrealNativeObjectStatus.Success;
        }
        public UnrealNativeObjectStatus CloseNativeObjectScope(ulong scope)
        {
            if (CloseStatus != UnrealNativeObjectStatus.Success) return CloseStatus;
            return Scopes.Remove(scope) ? UnrealNativeObjectStatus.Success : UnrealNativeObjectStatus.InvalidScope;
        }
        public UnrealNativeObjectStatus ReleaseNativeObject(ulong scope, UnrealNativeObjectHandle handle)
        {
            Releases++;
            return ReleaseStatus;
        }
        public UnrealNativeObjectStatus InvokeNativeObject(ulong scope, UnrealNativeObjectHandle target,
            ulong memberId, ReadOnlySpan<UnrealNativeValueV3> arguments, out UnrealNativeObjectCallResult result)
        {
            result = new(default, new(++_next));
            return Scopes.Contains(scope) ? UnrealNativeObjectStatus.Success : UnrealNativeObjectStatus.InvalidScope;
        }
        public UnrealNativeObjectStatus BeginNativeObjectText(ulong scope, UnrealNativeObjectHandle target, ulong memberId,
            ReadOnlySpan<UnrealNativeValueV3> arguments, ReadOnlySpan<byte> input, out ulong token, out uint requiredBytes)
        {
            token = ++_next; requiredBytes = 3; _pendingText.Add(token);
            return UnrealNativeObjectStatus.Success;
        }
        public UnrealNativeObjectStatus ReadNativeObjectText(ulong scope, ulong token, Span<byte> output,
            out uint requiredBytes, out UnrealNativeObjectCallResult result)
        {
            requiredBytes = 3; result = default;
            if (!_pendingText.Contains(token)) return UnrealNativeObjectStatus.InvalidTextToken;
            if (output.Length < 3) return UnrealNativeObjectStatus.TextBufferTooSmall;
            "abc"u8.CopyTo(output[..3]);
            _pendingText.Remove(token);
            result = new(new() { Kind = UnrealNativeValueKindV3.String, Flags = UnrealNativeValueFlagsV3.ArenaBacked, Payload1 = 3 }, default);
            return UnrealNativeObjectStatus.Success;
        }
        public UnrealNativeObjectStatus DiscardNativeObjectText(ulong scope, ulong token) =>
            _pendingText.Remove(token) ? UnrealNativeObjectStatus.Success : UnrealNativeObjectStatus.InvalidTextToken;
        public UnrealInvocationResponse ReadProperty(UnrealObjectHandle target, string path) => throw new NotSupportedException();
        public UnrealInvocationResponse WriteProperty(UnrealObjectHandle target, string path, UnrealInvocationValue value) => throw new NotSupportedException();
        public UnrealInvocationResponse Invoke(UnrealObjectHandle target, string path, IReadOnlyList<UnrealInvocationValue> arguments) => throw new NotSupportedException();
    }
}
