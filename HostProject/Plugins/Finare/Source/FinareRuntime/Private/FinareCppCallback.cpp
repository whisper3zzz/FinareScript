#include "FinareCppCallback.h"
#include "FinareNativeData.h"
#include "Misc/ScopeExit.h"
#include "FinareNativeTransport.h"

TWeakPtr<FFinareCppCallbackEndpoint> FFinareNativeBridgeContext::ResolveCppCallback(uint64 Scope, uint64 Token, uint64 Signature) const
{
	if (!IsInGameThread() || !CallbackScopes.Contains(Scope)) return {};
	const auto* Slot = CppCallbacks.Find(Token);
	return Slot && (*Slot)->Scope == Scope && (*Slot)->Signature == Signature && (*Slot)->Handler
		? TWeakPtr<FFinareCppCallbackEndpoint>(*Slot) : TWeakPtr<FFinareCppCallbackEndpoint>();
}

TWeakPtr<FFinareCppCallbackEndpoint> FFinareNativeObjectRegistry::ResolveCppCallback(const FFinareNativeValueV3& Value, uint64 Signature) const
{
	if (!CallbackContext || Value.Kind != EFinareNativeValueKindV3::Callback || Value.Flags != EFinareNativeValueFlagsV3::None ||
		Value.Reserved16 || Value.Auxiliary || Value.Payload2 || Value.Payload3 || !Signature || Value.TypeId != Signature) return {};
	return CallbackContext->ResolveCppCallback(Value.Payload1, Value.Payload0, Signature);
}

namespace
{
	TMap<FString, int32>& Signatures() { static auto* Value = new TMap<FString, int32>(); return *Value; }
}
FFinareCppCallbackSignature::FFinareCppCallbackSignature(const TCHAR* InPath) : Path(InPath) { ++Signatures().FindOrAdd(Path); }
FFinareCppCallbackSignature::~FFinareCppCallbackSignature() { if (--Signatures().FindChecked(Path) == 0) Signatures().Remove(Path); }
bool FFinareCppCallbackSignature::Contains(const FString& Path) { return Signatures().Contains(Path); }

bool FFinareCppCallbackEndpoint::Invoke(TConstArrayView<FFinareNativeValueV3> Arguments, FFinareNativeValueV3& Result) const
{
	if (!IsInGameThread() || Finare_IsNativeMemoryBorrowed() || !Handler) return false;
	FFinareCallbackRequest Request;
	Request.Token = Token; Request.Scope = Scope; Request.SignatureId = Signature;
	Request.Arguments = Arguments.GetData(); Request.ArgumentCount = Arguments.Num();
	FFinareCallbackResponse Response;
	Handler(UserData, &Request, &Response);
	ON_SCOPE_EXIT { if (Response.Release) Response.Release(Response.Owner); };
	if (Response.AbiVersion != 1 || Response.Size != sizeof(Response) || Response.Status != EFinareCallbackStatus::Success ||
		Response.Reserved0 || Response.Reserved1 || Response.Reserved2 || Response.ResultCount > 1 ||
		(Response.ResultCount && !Response.Results) || Response.ByteCount || !Response.Release || !Response.Owner) return false;
	Result = Response.ResultCount ? Response.Results[0] : FFinareNativeValueV3{}; return true;
}
