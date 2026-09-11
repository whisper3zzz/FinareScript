#if WITH_DEV_AUTOMATION_TESTS
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "FinareCallbackService.h"
#include "FinareNativeValueCodec.h"
#include "FinareWorldSubsystem.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareCorrelatedCallTest,
                                 "Finare.Unreal.NativeTransport.CorrelatedResults",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareCorrelatedCallTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::NativeTransport::Private;
	using Status = EFinareNativeCallStatusV3;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Correlated World"), World))
	{
		return false;
	}
	{
		FFinareObjectRegistry Registry(World);
		FFinareInvocationBridge Bridge(Registry);
		auto* Context = new FFinareNativeBridgeContext(Bridge);
		const auto* Table = Finare_GetNativeTransportV3();
		TStrongObjectPtr<UFinareCallbackService> Service(NewObject<UFinareCallbackService>(World));
		FFinareObjectHandleResult HandleResult;
		const auto Handle = Registry.RegisterObject(Service.Get(), HandleResult);
		const uint64 Member = FinareMemberId("/Script/FinareRuntime.FinareCallbackService:FormatReply");
		auto Call = [&](uint64 Id, const FString& Input, uint32 Capacity, FString& Output)
		{
			TArray<uint8> Arena;
			Arena.SetNum(Capacity);
			FFinareNativeValueV3 Argument, Result;
			Result.Kind = EFinareNativeValueKindV3::String;
			Result.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
			FFinareNativeCallFrameV3 Frame;
			Frame.ByteBuffer = Arena.GetData();
			Frame.ByteCapacity = Capacity;
			uint32 Bytes = 0;
			if (!EncodeValueV3(FFinareValue::FromString(Input), Frame, Argument, Bytes, 0))
			{
				return Status::InvalidFrame;
			}
			Frame.ByteCount = Bytes;
			Frame.Arguments = &Argument;
			Frame.ArgumentCount = 1;
			Frame.Results = &Result;
			Frame.ResultCount = Frame.ResultCapacity = 1;
			Table->InvokeCorrelatedById(Context, Id, Handle.Value, Member, &Frame);
			if (Frame.Status == Status::Success)
			{
				FFinareValue Value;
				if (!DecodeValueV3(Result, Frame, Frame.ByteCount, Value, 0))
				{
					return Status::InvalidFrame;
				}
				Output = Value.StringValue;
			}
			return Frame.Status;
		};
		FString Output;
		const uint64 Before = Bridge.GetFastInvocationCount();
		TestTrue(TEXT("First pending call"), Call(1, TEXT("first"), 64, Output) == Status::BufferTooSmall);
		TestTrue(TEXT("Second pending call"), Call(2, TEXT("second"), 64, Output) == Status::BufferTooSmall);
		TestEqual(TEXT("Independent pending results"), Context->GetCorrelatedResultCount(), 2);
		TestTrue(
			TEXT("Reject different request under pending identity"),
			Call(1, TEXT("changed"), 16384, Output) == Status::InvalidFrame);
		TestTrue(TEXT("Retry second first"), Call(2, TEXT("second"), 16384, Output) == Status::Success);
		TestEqual(TEXT("Second result preserved"), Output, FString::ChrN(9000, TEXT('x')) + TEXT("second"));
		TestTrue(TEXT("Retry first later"), Call(1, TEXT("first"), 16384, Output) == Status::Success);
		TestEqual(TEXT("First result preserved"), Output, FString::ChrN(9000, TEXT('x')) + TEXT("first"));
		TestEqual(TEXT("No retry or invalid identity executed gameplay again"), Bridge.GetFastInvocationCount(),
		          Before + 2);
		TestEqual(TEXT("Consumed entries removed"), Context->GetCorrelatedResultCount(), 0);
		TestTrue(TEXT("Zero correlation rejected"), Call(0, TEXT("zero"), 64, Output) == Status::InvalidFrame);

		for (uint64 Id = 1; Id <= Context->MaximumCorrelatedResults; ++Id)
		{
			TestTrue(TEXT("Fill bounded cache"), Call(Id, TEXT("bounded"), 64, Output) == Status::BufferTooSmall);
		}
		const uint64 FullCalls = Bridge.GetFastInvocationCount();
		TestTrue(
			TEXT("Full cache rejects before executing"),
			Call(1000, TEXT("extra"), 64, Output) == Status::TransportUnavailable);
		TestEqual(TEXT("No side effect on overflow"), Bridge.GetFastInvocationCount(), FullCalls);
		for (uint64 Id = 1; Id <= Context->MaximumCorrelatedResults; ++Id)
		{
			Table->ReleaseCorrelatedResult(Context, Id);
		}
		Table->ReleaseCorrelatedResult(Context, 1);
		TestEqual(TEXT("Abandoned entries released idempotently"), Context->GetCorrelatedResultCount(), 0);

		// Exercise accounting without allocating the entire budget in a test.
		auto Reserve = [](FFinareInvocationBridge&, FFinareNativeBridgeContext::FPendingCallFrameResult& Pending)
		{
			Pending.bValid = true;
			Pending.EncodedByteCount = FFinareNativeBridgeContext::MaximumCorrelatedResultBytes;
		};
		TestTrue(TEXT("Byte budget accepted"), Context->ExecuteCallFrame(Reserve, 500));
		TestFalse(TEXT("Byte budget cannot be exceeded"), Context->ExecuteCallFrame(Reserve, 501));
		TestEqual(TEXT("Overflow does not evict another result"), Context->GetCorrelatedResultCount(), 1);
		Context->Detach();
		TestEqual(TEXT("World detach releases pending data"), Context->GetCorrelatedResultCount(), 0);
		Context->Release();
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	return true;
}
#endif
