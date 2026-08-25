using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using UnifiedScript.Unreal;
using GeneratedComponent = UnifiedScript.Unreal.Generated.UnifiedScriptComponent;

namespace UnifiedScript.Tests;

public sealed class InProcessUnrealNativeInvocationTransportTests : IDisposable
{
    [ThreadStatic]
    private static InProcessUnrealNativeInvocationTransportTests? _activeV3Fixture;

    private const string TypePath =
        "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent";
    private const string ScriptAssetPath = TypePath + ":ScriptAssetPath";
    private const string AddPath = TypePath + ":Add";

    private readonly UnrealNativeReadPropertyV1 _read;
    private readonly UnrealNativeWritePropertyV1 _write;
    private readonly UnrealNativeInvokeV1 _invoke;
    private readonly UnrealNativeReadPropertyByIdV2 _fastRead;
    private readonly UnrealNativeWritePropertyByIdV2 _fastWrite;
    private readonly UnrealNativeInvokeByIdV2 _fastInvoke;
    private readonly UnrealNativeRetainContextV1 _retain;
    private readonly UnrealNativeReleaseContextV1 _release;
    private nint _tablePointer;
    private string _scriptAsset = string.Empty;
    private int _retainedContexts;
    private int _fastCalls;
    private int _callFrameCalls;

    public InProcessUnrealNativeInvocationTransportTests()
    {
        _read = Read;
        _write = Write;
        _invoke = Invoke;
        _fastRead = ReadById;
        _fastWrite = WriteById;
        _fastInvoke = InvokeById;
        _retain = Retain;
        _release = Release;
        var table = new TestTable
        {
            AbiVersion = 1,
            TableSize = checked((uint)Marshal.SizeOf<TestTable>()),
            ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
            ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
            WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
            Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
            RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
        };
        _tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTable>());
        Marshal.StructureToPtr(table, _tablePointer, false);
    }

    [Fact]
    public void GeneratedWrapperCrossesVersionedFunctionTable()
    {
        using (var transport = new InProcessUnrealNativeInvocationTransport(
            context: new nint(0x1234),
            _tablePointer))
        {
            var wrapper = new GeneratedComponent(
                UnrealObjectHandle.Create(slotIndex: 4, generation: 9),
                new NativeUnrealInvocationBridge(transport));

            wrapper.ScriptAssetPath = "/Game/Scripts/FunctionTable";

            Assert.Equal("/Game/Scripts/FunctionTable", wrapper.ScriptAssetPath);
            Assert.Equal(42, wrapper.Add(17, 25));
            Assert.Equal(1, _retainedContexts);
        }
        Assert.Equal(0, _retainedContexts);
    }

    [Fact]
    public void AbiV2GeneratedWrapperUsesAppendedMemberIdFunctions()
    {
        var table = new TestTableV2
        {
            AbiVersion = 2,
            TableSize = checked((uint)Marshal.SizeOf<TestTableV2>()),
            ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
            ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
            WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
            Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
            RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
            ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
            WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
            InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
        };
        nint tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2>());
        try
        {
            Marshal.StructureToPtr(table, tablePointer, false);
            using var transport = new InProcessUnrealNativeInvocationTransport(
                context: new nint(0x1234),
                tablePointer);
            Assert.True(transport.SupportsFastInvocation);
            var wrapper = new GeneratedComponent(
                UnrealObjectHandle.Create(slotIndex: 4, generation: 9),
                new NativeUnrealInvocationBridge(transport));

            wrapper.ScriptAssetPath = "/Game/Scripts/AbiV2";
            Assert.Equal("/Game/Scripts/AbiV2", wrapper.ScriptAssetPath);
            Assert.Equal(42, wrapper.Add(21, 21));
            Assert.Equal(3, _fastCalls);
        }
        finally
        {
            Marshal.FreeHGlobal(tablePointer);
        }
        Assert.Equal(0, _retainedContexts);
    }

    [Fact]
    public unsafe void AbiV2BootstrapDiscoversV3CallFrameAndKeepsLegacyFallback()
    {
        _activeV3Fixture = this;
        nint callFrame = (nint)(delegate* unmanaged[Cdecl]<
            nint,
            ulong,
            ulong,
            UnrealNativeCallFrameV3*,
            void>)&ExecuteByIdV3Unmanaged;
        var tableV3 = new UnrealNativeTransportTableV3
        {
            AbiVersion = UnrealNativeAbiV3.Version,
            TableSize = UnrealNativeAbiV3.TransportTableSize,
            ValueSize = UnrealNativeAbiV3.ValueSize,
            CallFrameSize = UnrealNativeAbiV3.CallFrameSize,
            Capabilities = UnrealNativeCapabilitiesV3.FixedScalars,
            RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
            ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
            ReadPropertyById = callFrame,
            WritePropertyById = callFrame,
            InvokeById = callFrame,
        };
        nint tableV3Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnrealNativeTransportTableV3>());
        nint tablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TestTableV2Extended>());
        try
        {
            Marshal.StructureToPtr(tableV3, tableV3Pointer, false);
            var table = new TestTableV2Extended
            {
                AbiVersion = 2,
                TableSize = checked((uint)Marshal.SizeOf<TestTableV2Extended>()),
                ValueSize = checked((uint)Marshal.SizeOf<TestValue>()),
                ReadProperty = Marshal.GetFunctionPointerForDelegate(_read),
                WriteProperty = Marshal.GetFunctionPointerForDelegate(_write),
                Invoke = Marshal.GetFunctionPointerForDelegate(_invoke),
                RetainContext = Marshal.GetFunctionPointerForDelegate(_retain),
                ReleaseContext = Marshal.GetFunctionPointerForDelegate(_release),
                ReadPropertyById = Marshal.GetFunctionPointerForDelegate(_fastRead),
                WritePropertyById = Marshal.GetFunctionPointerForDelegate(_fastWrite),
                InvokeById = Marshal.GetFunctionPointerForDelegate(_fastInvoke),
                CallFrameTransport = tableV3Pointer,
            };
            Marshal.StructureToPtr(table, tablePointer, false);

            using var transport = new InProcessUnrealNativeInvocationTransport(
                context: new nint(0x1234),
                tablePointer);
            Assert.Equal(
                UnrealNativeCapabilitiesV3.FixedScalars,
                transport.Capabilities);
            var bridge = new NativeUnrealInvocationBridge(transport);
            var wrapper = new GeneratedComponent(
                UnrealObjectHandle.Create(slotIndex: 4, generation: 9),
                bridge);

            wrapper.ScriptAssetPath = "/Game/Scripts/V3Bootstrap";
            Assert.Equal("/Game/Scripts/V3Bootstrap", wrapper.ScriptAssetPath);
            Assert.Equal(42, wrapper.Add(20, 22));
            Assert.Equal(1, _callFrameCalls);
            Assert.Equal(2, _fastCalls);
            Assert.Equal(1, bridge.CallFrameInvocationCount);
            Assert.Equal(2, bridge.LegacyInvocationCount);
        }
        finally
        {
            Marshal.FreeHGlobal(tablePointer);
            Marshal.FreeHGlobal(tableV3Pointer);
            _activeV3Fixture = null;
        }
        Assert.Equal(0, _retainedContexts);
    }

    [Fact]
    public void FunctionTableFailurePreservesNativeCategories()
    {
        var table = Marshal.PtrToStructure<TestTable>(_tablePointer);
        UnrealNativeReadPropertyV1 staleRead =
            static (context, target, path, pathLength, callback, userData) =>
            {
                InvokeCallback(
                    callback,
                    userData,
                    success: false,
                    error: UnrealInvocationError.ObjectHandleFailure,
                    handleError: UnrealObjectHandleError.StaleHandle,
                    message: "The native handle is stale.");
            };
        table.ReadProperty = Marshal.GetFunctionPointerForDelegate(staleRead);
        Marshal.StructureToPtr(table, _tablePointer, false);
        using var transport = new InProcessUnrealNativeInvocationTransport(
            context: new nint(0x1234),
            _tablePointer);

        UnrealInvocationResponse response = transport.ReadProperty(
            UnrealObjectHandle.Create(slotIndex: 4, generation: 9),
            ScriptAssetPath);

        Assert.False(response.IsSuccess);
        Assert.Equal(UnrealInvocationError.ObjectHandleFailure, response.Error);
        Assert.Equal(UnrealObjectHandleError.StaleHandle, response.HandleError);
        GC.KeepAlive(staleRead);
    }

    [Fact]
    public void ConstructorRejectsAbiLayoutMismatch()
    {
        TestTable table = Marshal.PtrToStructure<TestTable>(_tablePointer);
        table.ValueSize++;
        Marshal.StructureToPtr(table, _tablePointer, false);

        Assert.Throws<InvalidOperationException>(() =>
            new InProcessUnrealNativeInvocationTransport(
                context: new nint(0x1234),
                _tablePointer));
        Assert.Equal(0, _retainedContexts);
    }

    [Fact]
    public void DisposeReleasesContextAndRejectsFurtherCalls()
    {
        var transport = new InProcessUnrealNativeInvocationTransport(
            context: new nint(0x1234),
            _tablePointer);
        Assert.Equal(1, _retainedContexts);

        transport.Dispose();
        transport.Dispose();

        Assert.Equal(0, _retainedContexts);
        Assert.Throws<ObjectDisposedException>(() => transport.ReadProperty(
            UnrealObjectHandle.Create(slotIndex: 4, generation: 9),
            ScriptAssetPath));
    }

    public void Dispose()
    {
        if (_tablePointer != nint.Zero)
        {
            Marshal.FreeHGlobal(_tablePointer);
            _tablePointer = nint.Zero;
        }
        GC.KeepAlive(_read);
        GC.KeepAlive(_write);
        GC.KeepAlive(_invoke);
        GC.KeepAlive(_fastRead);
        GC.KeepAlive(_fastWrite);
        GC.KeepAlive(_fastInvoke);
        GC.KeepAlive(_retain);
        GC.KeepAlive(_release);
    }

    private byte Retain(nint context)
    {
        if (context != new nint(0x1234))
        {
            return 0;
        }
        _retainedContexts++;
        return 1;
    }

    private void Release(nint context)
    {
        Assert.Equal(new nint(0x1234), context);
        _retainedContexts--;
    }

    private void Read(
        nint context,
        ulong target,
        nint path,
        int pathLength,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        string memberPath = ReadUtf8(path, pathLength);
        if (memberPath == ScriptAssetPath)
        {
            InvokeCallback(
                callback,
                userData,
                success: true,
                valueKind: UnrealInvocationValueKind.String,
                stringValue: _scriptAsset);
            return;
        }
        InvokeCallback(
            callback,
            userData,
            success: false,
            error: UnrealInvocationError.PropertyNotFound,
            message: "Property not found.");
    }

    private void Write(
        nint context,
        ulong target,
        nint path,
        int pathLength,
        nint valuePointer,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        TestValue value = Marshal.PtrToStructure<TestValue>(valuePointer);
        if (ReadUtf8(path, pathLength) == ScriptAssetPath &&
            value.Kind == (byte)UnrealInvocationValueKind.String)
        {
            _scriptAsset = ReadUtf8(value.StringData, value.StringLength);
            InvokeCallback(callback, userData, success: true);
            return;
        }
        InvokeCallback(
            callback,
            userData,
            success: false,
            error: UnrealInvocationError.ValueTypeMismatch,
            message: "Property value mismatch.");
    }

    private void Invoke(
        nint context,
        ulong target,
        nint path,
        int pathLength,
        nint arguments,
        int argumentCount,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        if (ReadUtf8(path, pathLength) != AddPath || argumentCount != 2)
        {
            InvokeCallback(
                callback,
                userData,
                success: false,
                error: UnrealInvocationError.ArgumentCountMismatch,
                message: "Function arguments did not match.");
            return;
        }

        int valueSize = Marshal.SizeOf<TestValue>();
        TestValue left = Marshal.PtrToStructure<TestValue>(arguments);
        TestValue right = Marshal.PtrToStructure<TestValue>(arguments + valueSize);
        InvokeCallback(
            callback,
            userData,
            success: true,
            valueKind: UnrealInvocationValueKind.Int32,
            int32Value: left.Int32Value + right.Int32Value);
    }

    private void ReadById(
        nint context,
        ulong target,
        ulong memberId,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        _fastCalls++;
        if (memberId == UnrealMemberId.FromPath(ScriptAssetPath))
        {
            InvokeCallback(
                callback,
                userData,
                success: true,
                valueKind: UnrealInvocationValueKind.String,
                stringValue: _scriptAsset);
            return;
        }
        InvokeCallback(
            callback,
            userData,
            success: false,
            error: UnrealInvocationError.InvalidMemberPath,
            message: "Fast property ID was not found.");
    }

    private void WriteById(
        nint context,
        ulong target,
        ulong memberId,
        nint valuePointer,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        _fastCalls++;
        TestValue value = Marshal.PtrToStructure<TestValue>(valuePointer);
        if (memberId == UnrealMemberId.FromPath(ScriptAssetPath) &&
            value.Kind == (byte)UnrealInvocationValueKind.String)
        {
            _scriptAsset = ReadUtf8(value.StringData, value.StringLength);
            InvokeCallback(callback, userData, success: true);
            return;
        }
        InvokeCallback(
            callback,
            userData,
            success: false,
            error: UnrealInvocationError.ValueTypeMismatch,
            message: "Fast property value mismatch.");
    }

    private void InvokeById(
        nint context,
        ulong target,
        ulong memberId,
        nint arguments,
        int argumentCount,
        UnrealNativeResultCallbackV1 callback,
        nint userData)
    {
        _fastCalls++;
        if (memberId != UnrealMemberId.FromPath(AddPath) || argumentCount != 2)
        {
            InvokeCallback(
                callback,
                userData,
                success: false,
                error: UnrealInvocationError.ArgumentCountMismatch,
                message: "Fast function arguments did not match.");
            return;
        }
        int valueSize = Marshal.SizeOf<TestValue>();
        TestValue left = Marshal.PtrToStructure<TestValue>(arguments);
        TestValue right = Marshal.PtrToStructure<TestValue>(arguments + valueSize);
        InvokeCallback(
            callback,
            userData,
            success: true,
            valueKind: UnrealInvocationValueKind.Int32,
            int32Value: left.Int32Value + right.Int32Value);
    }

    private unsafe void ExecuteByIdV3(
        nint context,
        ulong target,
        ulong memberId,
        ref UnrealNativeCallFrameV3 frame)
    {
        _callFrameCalls++;
        if (context != new nint(0x1234) ||
            memberId != UnrealMemberId.FromPath(AddPath) ||
            frame.ArgumentCount != 2 ||
            frame.Arguments == nint.Zero ||
            frame.ResultCapacity < 1 ||
            frame.ResultCount != 1 ||
            frame.Results == nint.Zero)
        {
            frame.Status = UnrealNativeCallStatusV3.InvalidFrame;
            return;
        }
        var arguments = (UnrealNativeValueV3*)frame.Arguments;
        var results = (UnrealNativeValueV3*)frame.Results;
        UnrealNativeValueV3 left = arguments[0];
        UnrealNativeValueV3 right = arguments[1];
        UnrealNativeValueV3 descriptor = results[0];
        if (left.Kind != UnrealNativeValueKindV3.Int32 ||
            right.Kind != UnrealNativeValueKindV3.Int32 ||
            descriptor.Kind != UnrealNativeValueKindV3.Int32)
        {
            frame.Status = UnrealNativeCallStatusV3.InvalidFrame;
            return;
        }
        var result = new UnrealNativeValueV3
        {
            Kind = UnrealNativeValueKindV3.Int32,
            Payload0 = unchecked((ulong)(
                unchecked((int)left.Payload0) + unchecked((int)right.Payload0))),
        };
        results[0] = result;
        frame.ResultCount = 1;
        frame.RequiredResultCapacity = 1;
        frame.RequiredByteCapacity = frame.ByteCount;
        frame.Status = UnrealNativeCallStatusV3.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ExecuteByIdV3Unmanaged(
        nint context,
        ulong target,
        ulong memberId,
        UnrealNativeCallFrameV3* frame)
    {
        if (frame != null)
        {
            _activeV3Fixture?.ExecuteByIdV3(context, target, memberId, ref *frame);
        }
    }

    private static void InvokeCallback(
        UnrealNativeResultCallbackV1 callback,
        nint userData,
        bool success,
        UnrealInvocationValueKind valueKind = UnrealInvocationValueKind.Void,
        bool booleanValue = false,
        int int32Value = 0,
        string stringValue = "",
        UnrealInvocationError error = UnrealInvocationError.None,
        UnrealObjectHandleError handleError = UnrealObjectHandleError.None,
        string message = "")
    {
        using var valueBuffer = new TestUtf8Buffer(stringValue);
        using var messageBuffer = new TestUtf8Buffer(message);
        callback(
            userData,
            success ? (byte)1 : (byte)0,
            (byte)valueKind,
            booleanValue ? (byte)1 : (byte)0,
            int32Value,
            valueBuffer.Pointer,
            valueBuffer.Length,
            (byte)error,
            (byte)handleError,
            messageBuffer.Pointer,
            messageBuffer.Length);
    }

    private static string ReadUtf8(nint data, int length) =>
        length == 0
            ? string.Empty
            : Marshal.PtrToStringUTF8(data, length)!;

    [StructLayout(LayoutKind.Sequential)]
    private struct TestTable
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestTableV2
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
        public nint ReadPropertyById;
        public nint WritePropertyById;
        public nint InvokeById;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestTableV2Extended
    {
        public uint AbiVersion;
        public uint TableSize;
        public uint ValueSize;
        public uint Reserved;
        public nint ReadProperty;
        public nint WriteProperty;
        public nint Invoke;
        public nint RetainContext;
        public nint ReleaseContext;
        public nint ReadPropertyById;
        public nint WritePropertyById;
        public nint InvokeById;
        public nint CallFrameTransport;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestValue
    {
        public byte Kind;
        public byte BooleanValue;
        public ushort Reserved;
        public int Int32Value;
        public nint StringData;
        public int StringLength;
    }

    private sealed class TestUtf8Buffer : IDisposable
    {
        public TestUtf8Buffer(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Length = bytes.Length;
            Pointer = Marshal.AllocHGlobal(Math.Max(1, Length));
            if (Length > 0)
            {
                Marshal.Copy(bytes, 0, Pointer, Length);
            }
        }

        public nint Pointer { get; private set; }

        public int Length { get; }

        public void Dispose()
        {
            if (Pointer != nint.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = nint.Zero;
            }
        }
    }
}
