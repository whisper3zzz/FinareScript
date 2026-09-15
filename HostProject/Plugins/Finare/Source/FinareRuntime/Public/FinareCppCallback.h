#pragma once
#include "CoreMinimal.h"
#include "FinareCallbackTransport.h"
#include "FinareNativeObjectValues.h"
#include <functional>

class FINARERUNTIME_API FFinareCppCallbackSignature final
{
public:
	explicit FFinareCppCallbackSignature(const TCHAR* InPath);
	~FFinareCppCallbackSignature();
	static bool Contains(const FString& Path);
private:
	FString Path;
};

/** Generation-owned endpoint. std::function copies hold only a weak reference. */
struct FINARERUNTIME_API FFinareCppCallbackEndpoint
{
	uint64 Token = 0, Scope = 0, Signature = 0;
	FFinareCallback Handler = nullptr;
	void* UserData = nullptr;
	void Retire() { Handler = nullptr; UserData = nullptr; }
	bool Invoke(TConstArrayView<FFinareNativeValueV3> Arguments, FFinareNativeValueV3& Result) const;
};

namespace UE::Finare::NativeObjectValues
{
	template <typename R, typename... A>
	std::function<R(A...)> MakeStdFunction(TWeakPtr<FFinareCppCallbackEndpoint> Endpoint)
	{
		return [Endpoint](A... Arguments) -> R
		{
			if (!IsInGameThread()) { if constexpr (std::is_void_v<R>) return; else return R{}; }
			const auto Live = Endpoint.Pin();
			FFinareNativeValueV3 Values[] = {Write(Arguments)..., {}};
			FFinareNativeValueV3 Result;
			const bool Success = Live && Live->Invoke(MakeArrayView(Values, sizeof...(A)), Result);
			if constexpr (!std::is_void_v<R>)
			{
				R Return{}; if (Success) Read(Result, Return); return Return;
			}
		};
	}
}
