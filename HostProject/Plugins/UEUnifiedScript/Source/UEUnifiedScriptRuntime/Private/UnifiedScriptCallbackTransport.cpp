#include "UnifiedScriptCallbackTransport.h"
#include "UnifiedScriptCallbackProxy.h"
#include "UnifiedScriptNativeValueCodec.h"
#include "Containers/StringConv.h"
#include "Misc/ScopeExit.h"
#include "UObject/Field.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace
{
using Status = EUnifiedScriptCallbackStatus;
constexpr uint32 CallbackMaximumBytes = 16u * 1024u * 1024u;
uint64 NextScope = 0; // Game-thread-only; never recycled across Worlds.
}

UFunction* UUnifiedScriptCallbackProxy::DispatchFunction(UFunction* Signature)
{
    const uint64 Id = UnifiedScriptMemberId(TCHAR_TO_UTF8(*Signature->GetPathName()));
    const FName Name(*FString::Printf(TEXT("UnifiedScriptCallback_%016llX"), static_cast<unsigned long long>(Id)));
    UClass* Owner = StaticClass();
    if (UFunction* Existing = Owner->FindFunctionByName(Name, EIncludeSuperFlag::ExcludeSuper))
        return Existing->IsSignatureCompatibleWith(Signature) ? Existing : nullptr;
    UFunction* Function = NewObject<UFunction>(Owner, Name, RF_Public | RF_Transient);
    Function->FunctionFlags = FUNC_Native | FUNC_Public;
    FField::FLinkedListBuilder Properties(&Function->ChildProperties);
    for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        if (!It->HasAnyPropertyFlags(CPF_Parm)) continue;
        FProperty* Clone = CastFieldChecked<FProperty>(FField::Duplicate(*It, Function, It->GetFName()));
        if (Clone->HasAnyPropertyFlags(CPF_OutParm)) Function->FunctionFlags |= FUNC_HasOutParms;
        Clone->Next = nullptr;
        Properties.AppendNoTerminate(*Clone);
    }
    Function->StaticLink(true);
    Function->InitializeDerivedMembers();
    Function->SetNativeFunc(&UUnifiedScriptCallbackProxy::execDispatch);
    Owner->AddFunctionToFunctionMap(Function, Name);
    return Function->IsSignatureCompatibleWith(Signature) ? Function : nullptr;
}

bool UUnifiedScriptCallbackProxy::Bind(FUnifiedScriptNativeBridgeContext* Context, uint64 Scope, uint64 Token,
    UFunction* Signature, FUnifiedScriptCallback Callback, void* UserData)
{
    check(IsInGameThread());
    if (CallbackContext || !Context || !Scope || !Token || !Signature || !Callback) return false;
    UFunction* Function = DispatchFunction(Signature);
    if (!Function) return false;
    CallbackContext = Context;
    CallbackScope = Scope;
    CallbackToken = Token;
    CallbackSignatureId = UnifiedScriptMemberId(TCHAR_TO_UTF8(*Signature->GetPathName()));
    Handler = Callback;
    HandlerData = UserData;
    BoundDelegate.BindUFunction(this, Function->GetFName());
    return true;
}

void UUnifiedScriptCallbackProxy::Unbind()
{
    check(IsInGameThread());
    CallbackContext = nullptr;
    Handler = nullptr;
    HandlerData = nullptr;
    BoundDelegate.Unbind();
}

bool UUnifiedScriptCallbackProxy::CopyDelegate(uint64 Scope, uint64 SignatureId, FScriptDelegate& OutDelegate) const
{
    if (!IsInGameThread() || !CallbackContext || !Handler || Scope != CallbackScope || SignatureId != CallbackSignatureId) return false;
    OutDelegate = BoundDelegate;
    return true;
}

DEFINE_FUNCTION(UUnifiedScriptCallbackProxy::execDispatch)
{
    UFunction* Function = Stack.CurrentNativeFunction;
    if (!Function) return;
    if (!IsInGameThread())
    {
        if (FProperty* Return = Function->GetReturnProperty(); Return && RESULT_PARAM) Return->ClearValue(RESULT_PARAM);
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback invocation requires the game thread."));
        return;
    }
    FStructOnScope OwnedParameters(Function);
    void* Memory = OwnedParameters.GetStructMemory();
    TMap<FName, void*> OutputDestinations;
    for (FOutParmRec* Out = Stack.OutParms; Out; Out = Out->NextOutParm)
        if (Out->Property && Out->PropAddr) OutputDestinations.Add(Out->Property->GetFName(), Out->PropAddr);
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        FProperty* Property = *It;
        if (!Property->HasAnyPropertyFlags(CPF_Parm) || Property->HasAnyPropertyFlags(CPF_ReturnParm)) continue;
        void* Destination = Property->ContainerPtrToValuePtr<void>(Memory);
        if (Stack.Node != Function)
        {
            Stack.MostRecentPropertyAddress = nullptr;
            Stack.Step(Stack.Object, Destination);
            if (Property->HasAnyPropertyFlags(CPF_OutParm) && Stack.MostRecentPropertyAddress)
                OutputDestinations.Add(Property->GetFName(), Stack.MostRecentPropertyAddress);
        }
        else
        {
            // ProcessEvent's Locals are a shallow copy of the caller's block.
            // Never mutate/destruct its FString/container storage. Own a deep copy
            // and publish ref/out to the original FOutParmRec addresses instead.
            void* const* Out = OutputDestinations.Find(Property->GetFName());
            const void* Source = Out ? *Out : Property->ContainerPtrToValuePtr<void>(Stack.Locals);
            Property->CopyCompleteValue(Destination, Source);
        }
    }
    if (Stack.Node != Function) { P_FINISH; }
    FProperty* Return = Function->GetReturnProperty();
    if (Return && RESULT_PARAM) Return->ClearValue(RESULT_PARAM);
    if (!CastChecked<UUnifiedScriptCallbackProxy>(Context)->Dispatch(Function, Memory)) return;
    if (Return && RESULT_PARAM) Return->CopyCompleteValue(RESULT_PARAM, Return->ContainerPtrToValuePtr<void>(Memory));
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        if (It->HasAllPropertyFlags(CPF_Parm | CPF_OutParm) && !It->HasAnyPropertyFlags(CPF_ReturnParm | CPF_ConstParm))
            if (void* const* Destination = OutputDestinations.Find(It->GetFName()))
                It->CopyCompleteValue(*Destination, It->ContainerPtrToValuePtr<void>(Memory));
}

bool UUnifiedScriptCallbackProxy::Dispatch(UFunction* Function, void* Parameters)
{
    using namespace UE::UnifiedScript::NativeTransport::Private;
    if (!IsInGameThread() || !Function || (Function->ParmsSize && !Parameters)) return false;
    if (FProperty* Return = Function->GetReturnProperty()) Return->ClearValue_InContainer(Parameters);
    auto* Context = CallbackContext;
    const auto Callback = Handler;
    void* UserData = HandlerData;
    if (!Context || !Callback || !Context->TryRetain()) return false;
    TStrongObjectPtr<UUnifiedScriptCallbackProxy> KeepAlive(this);
    ON_SCOPE_EXIT { Context->Release(); };
    TArray<FUnifiedScriptValue> Arguments;
    FUnifiedScriptInvocationResult Result;
    if (!Context->ExecuteGameThreadFast([&](auto& Bridge) { Bridge.ReadDelegateArguments(Function, Parameters, Arguments, Result); }) || !Result.IsSuccess())
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback arguments could not be read: %s"), *Result.Message);
        return false;
    }
    ON_SCOPE_EXIT { Context->ExecuteGameThreadFast([&](auto& Bridge) { Bridge.ReleaseEventArguments(Arguments); }); };
    if (Arguments.Num() > 16) return false;
    TArray<FUnifiedScriptNativeValueV3> Cells;
    Cells.SetNum(Arguments.Num());
    TArray<uint8> Bytes;
    uint32 ByteCount = 0;
    bool bEncoded = false;
    for (uint32 Capacity = 4096; Capacity <= CallbackMaximumBytes; Capacity *= 2)
    {
        Bytes.SetNumUninitialized(Capacity);
        FUnifiedScriptNativeCallFrameV3 Frame;
        Frame.ByteBuffer = Bytes.GetData();
        Frame.ByteCapacity = Capacity;
        ByteCount = 0;
        bEncoded = true;
        for (int32 Index = 0; Index < Arguments.Num(); ++Index)
            if (!EncodeValueV3(Arguments[Index], Frame, Cells[Index], ByteCount, 0)) { bEncoded = false; break; }
        if (bEncoded) break;
    }
    if (!bEncoded)
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback arguments cannot be encoded within the 16 MiB limit."));
        return false;
    }
    FUnifiedScriptCallbackRequest Request;
    Request.Token = CallbackToken;
    Request.Scope = CallbackScope;
    Request.SignatureId = CallbackSignatureId;
    Request.Arguments = Cells.GetData();
    Request.ArgumentCount = Cells.Num();
    Request.ByteBuffer = Bytes.GetData();
    Request.ByteCount = ByteCount;
    FUnifiedScriptCallbackResponse Response;
    Callback(UserData, &Request, &Response);
    ON_SCOPE_EXIT { if (Response.Release && Response.Owner) Response.Release(Response.Owner); };
    if (Response.AbiVersion != 1 || Response.Size != sizeof(Response) || Response.Status != Status::Success ||
        Response.Reserved0 || Response.Reserved1 || Response.Reserved2 || Response.ResultCount > 17 ||
        Response.ByteCount > CallbackMaximumBytes || (Response.ResultCount && !Response.Results) ||
        (Response.ByteCount && !Response.ByteBuffer) || !Response.Owner || !Response.Release)
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback failed or returned an invalid response (status %d)."), int32(Response.Status));
        return false;
    }
    FUnifiedScriptNativeCallFrameV3 OutputFrame;
    OutputFrame.ByteBuffer = const_cast<uint8*>(Response.ByteBuffer);
    OutputFrame.ByteCapacity = Response.ByteCount;
    TArray<FUnifiedScriptValue> Outputs;
    Outputs.SetNum(Response.ResultCount);
    for (int32 Index = 0; Index < Outputs.Num(); ++Index)
        if (!DecodeValueV3(Response.Results[Index], OutputFrame, Response.ByteCount, Outputs[Index], 0))
        {
            UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback output %d could not be decoded."), Index);
            return false;
        }
    if (!Context->ExecuteGameThreadFast([&](auto& Bridge) { Bridge.WriteCallbackOutputs(Function, Parameters, Outputs, Result); }) || !Result.IsSuccess())
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript callback outputs were not published: %s"), *Result.Message);
        return false;
    }
    return true;
}

EUnifiedScriptCallbackStatus FUnifiedScriptNativeBridgeContext::OpenCallbackScope(uint64& OutScope)
{
    OutScope = 0;
    if (!IsInGameThread()) return Status::WrongThread;
    Status Result = Status::Unavailable;
    ExecuteGameThreadFast([&](auto&)
    {
        if (NextScope == MAX_uint64) { Result = Status::Exhausted; return; }
        OutScope = ++NextScope;
        CallbackScopes.Add(OutScope);
        Result = Status::Success;
    });
    return Result;
}

EUnifiedScriptCallbackStatus FUnifiedScriptNativeBridgeContext::RegisterCallback(uint64 Scope, const FString& SignaturePath,
    FUnifiedScriptCallback Callback, void* UserData, uint64& OutToken)
{
    OutToken = 0;
    if (!IsInGameThread()) return Status::WrongThread;
    Status Result = Status::Unavailable;
    ExecuteGameThreadFast([&](auto& Bridge)
    {
        if (!CallbackScopes.Contains(Scope)) { Result = Status::InvalidScope; return; }
        UFunction* Signature = FindObject<UFunction>(nullptr, *SignaturePath);
        if (!Signature || !Signature->HasAnyFunctionFlags(FUNC_Delegate) || Signature->NumParms > 17 || !Callback)
        { Result = Status::WrongSignature; return; }
        TStrongObjectPtr<UObject> Proxy(NewObject<UUnifiedScriptCallbackProxy>());
        FUnifiedScriptObjectHandleResult HandleResult;
        const auto Token = Bridge.GetObjectRegistry().RegisterObject(Proxy.Get(), HandleResult);
        if (Token.IsNull()) { Result = Status::InvalidArgument; return; }
        if (!CastChecked<UUnifiedScriptCallbackProxy>(Proxy.Get())->Bind(this, Scope, Token.Value, Signature, Callback, UserData))
        {
            Bridge.GetObjectRegistry().ReleaseObject(Token, HandleResult);
            Result = Status::WrongSignature;
            return;
        }
        CallbackProxies.Add(Token.Value, MoveTemp(Proxy));
        OutToken = Token.Value;
        Result = Status::Success;
    });
    return Result;
}

EUnifiedScriptCallbackStatus FUnifiedScriptNativeBridgeContext::ReleaseCallback(uint64 Scope, uint64 Token)
{
    if (!IsInGameThread()) return Status::WrongThread;
    Status Result = Status::Unavailable;
    ExecuteGameThreadFast([&](auto& Bridge)
    {
        if (!CallbackScopes.Contains(Scope)) { Result = Status::InvalidScope; return; }
        auto* Entry = CallbackProxies.Find(Token);
        if (!Entry) { Result = Status::InvalidHandle; return; }
        auto* Proxy = CastChecked<UUnifiedScriptCallbackProxy>(Entry->Get());
        if (Proxy->GetScope() != Scope) { Result = Status::WrongOwner; return; }
        Proxy->Unbind();
        FUnifiedScriptObjectHandleResult HandleResult;
        Bridge.GetObjectRegistry().ReleaseObject({Token}, HandleResult);
        CallbackProxies.Remove(Token);
        Result = Status::Success;
    });
    return Result;
}

EUnifiedScriptCallbackStatus FUnifiedScriptNativeBridgeContext::CloseCallbackScope(uint64 Scope)
{
    if (!IsInGameThread()) return Status::WrongThread;
    Status Result = Status::Unavailable;
    ExecuteGameThreadFast([&](auto&)
    {
        if (!CallbackScopes.Contains(Scope)) { Result = Status::InvalidScope; return; }
        TArray<uint64> Tokens;
        for (const auto& Entry : CallbackProxies)
            if (CastChecked<UUnifiedScriptCallbackProxy>(Entry.Value.Get())->GetScope() == Scope) Tokens.Add(Entry.Key);
        for (uint64 Token : Tokens) ReleaseCallback(Scope, Token);
        CallbackScopes.Remove(Scope);
        Result = Status::Success;
    });
    return Result;
}

bool UEUnifiedScript_ResolveCallback(FUnifiedScriptObjectRegistry& Registry, const FUnifiedScriptValue& Value,
    uint64 SignatureId, FScriptDelegate& OutDelegate, FUnifiedScriptInvocationResult& OutResult)
{
    OutDelegate.Unbind();
    FUnifiedScriptObjectHandleResult HandleResult;
    auto* Proxy = Value.Kind == EUnifiedScriptValueKind::Callback && Value.TypeIdValue == SignatureId
        ? Cast<UUnifiedScriptCallbackProxy>(Registry.ResolveObject(Value.ObjectHandleValue, HandleResult)) : nullptr;
    if (Proxy && Proxy->CopyDelegate(Value.UInt64Value, SignatureId, OutDelegate)) return true;
    OutResult.Error = EUnifiedScriptInvocationError::ValueTypeMismatch;
    OutResult.Message = TEXT("Callback is unavailable or belongs to a different signature, generation or World.");
    return false;
}

extern "C" const FUnifiedScriptCallbackTransport* UEUnifiedScript_GetCallbackTransport()
{
    static const FUnifiedScriptCallbackTransport Table = {
        1, sizeof(FUnifiedScriptCallbackTransport), sizeof(FUnifiedScriptCallbackRequest), sizeof(FUnifiedScriptCallbackResponse),
        UEUnifiedScript_GetNativeTransportV1()->RetainContext, UEUnifiedScript_GetNativeTransportV1()->ReleaseContext,
        [](void* Context, uint64* Scope) { if (!Context || !Scope) return Status::InvalidArgument; return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->OpenCallbackScope(*Scope); },
        [](void* Context, uint64 Scope) { return Context ? static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->CloseCallbackScope(Scope) : Status::Unavailable; },
        [](void* Context, uint64 Scope, const uint8* Path, uint32 Length, FUnifiedScriptCallback Callback, void* UserData, uint64* Token)
        {
            if (Token) *Token = 0;
            if (!Context || !Path || !Length || Length > 4096 || !Token || !Callback) return Status::InvalidArgument;
            for (uint32 Index = 0; Index < Length; ++Index) if (!Path[Index]) return Status::InvalidArgument;
            FUTF8ToTCHAR Text(reinterpret_cast<const UTF8CHAR*>(Path), Length);
            const FString Signature(Text.Length(), Text.Get());
            FTCHARToUTF8 RoundTrip(*Signature);
            if (uint32(RoundTrip.Length()) != Length || FMemory::Memcmp(RoundTrip.Get(), Path, Length)) return Status::InvalidArgument;
            return static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->RegisterCallback(Scope, Signature, Callback, UserData, *Token);
        },
        [](void* Context, uint64 Scope, uint64 Token) { return Context ? static_cast<FUnifiedScriptNativeBridgeContext*>(Context)->ReleaseCallback(Scope, Token) : Status::Unavailable; }
    };
    return &Table;
}
