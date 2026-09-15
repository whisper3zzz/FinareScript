#include "FinareNativeObjectTransport.h"
#include "FinareNativeTransport.h"

namespace
{
	using EStatus = EFinareNativeObjectStatus;

	template <typename F>
	EStatus Execute(void* Context, F&& Operation)
	{
		if (!IsInGameThread())
		{
			return EStatus::WrongThread;
		}
		if (!Context)
		{
			return EStatus::Unavailable;
		}
		EStatus Status = EStatus::Unavailable;
		static_cast<FFinareNativeBridgeContext*>(Context)->ExecuteNativeObjects(
			[&](FFinareNativeObjectRegistry& Registry) { Status = Operation(Registry); });
		return Status;
	}

	EStatus OpenScope(void* Context, uint64* Scope)
	{
		if (!Scope)
		{
			return EStatus::InvalidArgument;
		}
		*Scope = 0;
		return Execute(Context, [&](auto& Registry) { return Registry.OpenScope(*Scope); });
	}

	EStatus CloseScope(void* Context, uint64 Scope)
	{
		return Execute(Context, [&](auto& Registry) { return Registry.CloseScope(Scope); });
	}

	EStatus ReleaseObject(void* Context, uint64 Scope, uint64 Handle)
	{
		return Execute(Context, [&](auto& Registry) { return Registry.Release(Scope, {Handle}); });
	}

	EStatus Invoke(void* Context, uint64 Scope, uint64 Target, uint64 MemberId,
	               const FFinareNativeValueV3* Arguments, uint32 Count,
	               FFinareNativeValueV3* OutValue, uint64* OutHandle)
	{
		if (OutValue)
		{
			*OutValue = {};
		}
		if (OutHandle)
		{
			*OutHandle = 0;
		}
		if (!OutValue || !OutHandle || Count > 16 || (Count && !Arguments))
		{
			return EStatus::InvalidArgument;
		}
		return Execute(Context, [&](auto& Registry)
		{
			FFinareNativeObjectHandle Handle;
			const auto Status = Registry.Invoke(Scope, {Target}, MemberId,
			                                    MakeArrayView(Arguments, static_cast<int32>(Count)), *OutValue, Handle);
			*OutHandle = Handle.Value;
			return Status;
		});
	}

	EStatus BeginText(void* Context, uint64 Scope, uint64 Target, uint64 MemberId,
	                  const FFinareNativeValueV3* Arguments, uint32 Count, const uint8* Input, uint32 InputBytes,
	                  uint64* Token, uint32* RequiredBytes)
	{
		if (!Token || !RequiredBytes || Count > 16 || InputBytes > 16u * 1024u * 1024u ||
			(Count && !Arguments) || (InputBytes && !Input))
		{
			return EStatus::InvalidArgument;
		}
		*Token = 0;
		*RequiredBytes = 0;
		return Execute(Context, [&](auto& Registry)
		{
			return Registry.BeginText(Scope, {Target}, MemberId,
			                          MakeArrayView(Arguments, static_cast<int32>(Count)),
			                          MakeArrayView(Input, static_cast<int32>(InputBytes)), *Token, *RequiredBytes);
		});
	}

	EStatus ReadText(void* Context, uint64 Scope, uint64 Token, uint8* Output, uint32 Capacity,
	                 uint32* RequiredBytes, FFinareNativeValueV3* OutValue, uint64* OutHandle)
	{
		if (!RequiredBytes || !OutValue || !OutHandle || (Capacity && !Output))
		{
			return EStatus::InvalidArgument;
		}
		*RequiredBytes = 0;
		*OutValue = {};
		*OutHandle = 0;
		return Execute(Context, [&](auto& Registry)
		{
			FFinareNativeObjectHandle Handle;
			const EStatus Status = Registry.ReadText(Scope, Token, Output, Capacity, *RequiredBytes, *OutValue, Handle);
			*OutHandle = Handle.Value;
			return Status;
		});
	}

	EStatus DiscardText(void* Context, uint64 Scope, uint64 Token)
	{
		return Execute(Context, [&](auto& Registry) { return Registry.DiscardText(Scope, Token); });
	}
	EStatus Borrow(void* Context, uint64 Scope, uint64 Target, uint64 Member, uint8 Kind, uint32 Size, uint8 Write,
		FFinareMemoryCallback Callback, void* UserData)
	{
		if (Write > 1) return EStatus::InvalidArgument;
		return Execute(Context, [&](auto& Registry) { return Registry.BorrowMemory(Scope, {Target}, Member, Kind, Size, Write != 0, Callback, UserData); });
	}
}

extern "C" const FFinareNativeObjectTransportV1* Finare_GetNativeObjectTransportV1()
{
	static const FFinareNativeObjectTextTransportV1 TextTable{
		1, sizeof(FFinareNativeObjectTextTransportV1), sizeof(FFinareNativeValueV3), 0,
		16u * 1024u * 1024u, 16u * 1024u * 1024u, 16u * 1024u * 1024u, 64,
		&BeginText, &ReadText, &DiscardText
	};
	static const FFinareNativeObjectTransportWithMemory Table{{
		{
			{
				1, sizeof(FFinareNativeObjectTransportWithMemory), sizeof(FFinareNativeValueV3), 0,
				Finare_GetNativeTransportV1()->RetainContext,
				Finare_GetNativeTransportV1()->ReleaseContext,
				&OpenScope, &CloseScope, &ReleaseObject, &Invoke
			},
			7
		},
		&TextTable
	}, &Borrow};
	return &Table.Base.Base.Base;
}
