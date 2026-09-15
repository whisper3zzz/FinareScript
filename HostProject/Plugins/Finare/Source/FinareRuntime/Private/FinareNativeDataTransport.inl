namespace UE::Finare::NativeTransport::Private
{
	void ExecuteNativeData(void* Context, uint64 Target, const uint8* PathData, int32 PathLength,
		uint8 Operation, FFinareNativeCallFrameV3* Frame)
	{
		if (!Frame) return;
		const bool Clean = Frame->Status == EFinareNativeCallStatusV3::Uninitialized && !Frame->InvocationError && !Frame->HandleError &&
			!Frame->RequiredResultCapacity && !Frame->RequiredByteCapacity && !Frame->ErrorMessageOffset && !Frame->ErrorMessageLength;
		Frame->Status = EFinareNativeCallStatusV3::InvalidFrame;
		if (!Clean || Frame->ResultCount != 1 || !Context || !IsInGameThread() || Finare_IsNativeMemoryBorrowed() || !PathData || PathLength <= 0 || PathLength > 4096 ||
			Frame->ArgumentCount > 2 || (Frame->ArgumentCount && !Frame->Arguments) || Frame->ResultCapacity < 1 || !Frame->Results ||
			Frame->Flags || Frame->Reserved8 || Frame->Reserved32 || Frame->ByteCount > Frame->ByteCapacity ||
			Frame->ByteCapacity > FFinareByteBuffer::MaximumCallBytes || (Frame->ByteCapacity && !Frame->ByteBuffer)) return;
		const uint32 InputBytes = Frame->ByteCount;
		TArray<FFinareValue> Arguments;
		for (uint32 Index = 0; Index < Frame->ArgumentCount; ++Index)
		{
			FFinareValue Value;
			if (!DecodeValueV3(Frame->Arguments[Index], *Frame, InputBytes, Value)) return;
			Arguments.Add(MoveTemp(Value));
		}
		FUTF8ToTCHAR Text(reinterpret_cast<const ANSICHAR*>(PathData), PathLength);
		const FString Path(Text.Length(), Text.Get());
		if (!static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteGameThreadFast([&](FFinareInvocationBridge& Bridge)
		{
			FFinareValue Value; FFinareInvocationResult Result;
			if (!Bridge.AccessNativeData({Target}, Path, Operation, Arguments, Value, Result))
			{ InvocationFailureV3(*Frame, InputBytes, Result); return; }
			uint32 Required = 0;
			if (!MeasureValueV3(Value, Required) || Required > FFinareByteBuffer::MaximumCallBytes) return;
			Frame->RequiredResultCapacity = 1; Frame->RequiredByteCapacity = Required;
			if (Required > Frame->ByteCapacity)
			{ Frame->Status = EFinareNativeCallStatusV3::BufferTooSmall; return; }
			uint32 Cursor = 0;
			if (Value.Kind == EFinareValueKind::Void) Frame->Results[0] = {};
			else if (!EncodeValueV3(Value, *Frame, Frame->Results[0], Cursor)) return;
			Frame->ByteCount = Cursor; Frame->ResultCount = 1; Frame->Status = EFinareNativeCallStatusV3::Success;
		})) Frame->Status = EFinareNativeCallStatusV3::TransportUnavailable;
	}
	uint8 BorrowNativeData(void* Context, uint64 Target, const uint8* PathData, int32 PathLength,
		uint8 Kind, uint32 Size, uint8 Write, FFinareMemoryCallback Callback, void* UserData, uint8* Error, int32 ErrorCapacity)
	{
		if (!Context || !IsInGameThread() || Finare_IsNativeMemoryBorrowed() || !Callback || !PathData ||
			PathLength <= 0 || PathLength > 4096 || Write > 1) return 0;
		FUTF8ToTCHAR Text(reinterpret_cast<const ANSICHAR*>(PathData), PathLength);
		FString Message; bool Success = false;
		static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteGameThreadFast([&](FFinareInvocationBridge& Bridge)
		{ Success = Bridge.BorrowNativeData({Target}, FString(Text.Length(), Text.Get()), Kind, Size, Write != 0, Callback, UserData, Message); });
		if (!Success && Error && ErrorCapacity > 0)
		{
			FTCHARToUTF8 Utf8(*Message); const int32 Count = FMath::Min(Utf8.Length(), ErrorCapacity - 1);
			FMemory::Memcpy(Error, Utf8.Get(), Count); Error[Count] = 0;
		}
		return Success;
	}
}

const FFinareNativeDataTransportV1* Finare_GetNativeDataTransportV1()
{
	using namespace UE::Finare::NativeTransport::Private;
	static const FFinareNativeDataTransportV1 Table{1, sizeof(FFinareNativeDataTransportV1), &ExecuteNativeData, &BorrowNativeData};
	return &Table;
}
