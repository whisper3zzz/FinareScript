#include "UnifiedScriptDataPlaneExtension.h"

namespace UE::UnifiedScript::DataPlaneExtension::Private
{
bool IsSafeIdentifier(const TCHAR* Value)
{
    if (Value == nullptr)
    {
        return false;
    }
    const FString Text(Value);
    if (Text.IsEmpty() || Text.Len() > 128)
    {
        return false;
    }
    for (TCHAR Character : Text)
    {
        if (!FChar::IsAlnum(Character) && Character != TEXT('.') &&
            Character != TEXT('-') && Character != TEXT('_'))
        {
            return false;
        }
    }
    return true;
}
}

bool FUnifiedScriptDataPlaneExtensionRegistry::Register(
    const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
    FString& OutError)
{
    check(IsInGameThread());
    OutError.Reset();
    using namespace UE::UnifiedScript::DataPlaneExtension::Private;
    if (!IsSafeIdentifier(Extension.Id) || Extension.Key == 0 ||
        Extension.Revision == 0 || Extension.SchemaFingerprint == 0 ||
        Extension.RequiredAbiVersion != UE::UnifiedScript::SharedData::AbiVersion ||
        Extension.RequiredLayoutCompatibilityId !=
            UE::UnifiedScript::DataPlaneSchema::CompatibilityId ||
        Extension.Channels == nullptr || Extension.ChannelCount == 0 ||
        Extension.ChannelCount > 64)
    {
        OutError = TEXT("The extension identity, ABI fence, or channel table is invalid.");
        return false;
    }

    if (const FRegisteredExtension* Existing = Extensions.Find(Extension.Key))
    {
        if (Existing->Id == Extension.Id && Existing->Revision == Extension.Revision &&
            Existing->SchemaFingerprint == Extension.SchemaFingerprint &&
            Existing->SchemaIds.Num() == static_cast<int32>(Extension.ChannelCount))
        {
            for (uint32 Index = 0; Index < Extension.ChannelCount; ++Index)
            {
                const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel =
                    Extension.Channels[Index];
                const FRegisteredChannel* Registered =
                    Channels.Find(Channel.SchemaId);
                if (!IsSafeIdentifier(Channel.Name) ||
                    !IsSafeIdentifier(Channel.ProfileBoundary) ||
                    Registered == nullptr ||
                    Registered->ExtensionKey != Extension.Key ||
                    Registered->ExtensionFingerprint != Extension.SchemaFingerprint ||
                    Registered->Name != Channel.Name ||
                    Registered->ProfileBoundary != Channel.ProfileBoundary ||
                    Registered->EntrySize != Channel.EntrySize ||
                    Registered->MaximumEntryCount != Channel.MaximumEntryCount ||
                    Registered->MaximumByteCount != Channel.MaximumByteCount ||
                    Registered->Kind != Channel.Kind ||
                    Registered->PublicationPhase != Channel.PublicationPhase)
                {
                    OutError = TEXT("The idempotent extension registration is incomplete.");
                    return false;
                }
            }
            return true;
        }
        OutError = TEXT("The extension key is already registered with different content.");
        return false;
    }

    TSet<uint64> LocalSchemaIds;
    TSet<FString> LocalNames;
    TArray<FRegisteredChannel> Pending;
    Pending.Reserve(Extension.ChannelCount);
    for (uint32 Index = 0; Index < Extension.ChannelCount; ++Index)
    {
        const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel =
            Extension.Channels[Index];
        const uint32 GlobalMaximumEntries =
            Channel.Kind == EUnifiedScriptDataPlaneExtensionChannelKind::Snapshot
                ? UE::UnifiedScript::SharedData::MaximumGeneratedSnapshotCount
                : UE::UnifiedScript::GameplayData::MaximumGeneratedEventCount;
        const uint64 RequiredBytes =
            static_cast<uint64>(Channel.EntrySize) * Channel.MaximumEntryCount;
        if (!IsSafeIdentifier(Channel.Name) || !IsSafeIdentifier(Channel.ProfileBoundary) ||
            Channel.SchemaId == 0 || Channel.EntrySize == 0 ||
            Channel.MaximumEntryCount == 0 ||
            Channel.MaximumEntryCount > GlobalMaximumEntries ||
            Channel.MaximumByteCount < RequiredBytes ||
            Channel.MaximumByteCount >
                UE::UnifiedScript::SharedData::MaximumGeneratedChannelBytes ||
            Channel.Kind != EUnifiedScriptDataPlaneExtensionChannelKind::Snapshot &&
                Channel.Kind != EUnifiedScriptDataPlaneExtensionChannelKind::Event ||
            Channel.PublicationPhase !=
                EUnifiedScriptDataPlaneExtensionPublicationPhase::Native ||
            LocalSchemaIds.Contains(Channel.SchemaId) ||
            LocalNames.Contains(FString(Channel.Name)) || Channels.Contains(Channel.SchemaId))
        {
            OutError = FString::Printf(
                TEXT("Extension channel %u has invalid identity, bounds, phase, or a collision."),
                Index);
            return false;
        }
        LocalSchemaIds.Add(Channel.SchemaId);
        LocalNames.Add(FString(Channel.Name));
        Pending.Add({
            Channel.Name,
            Channel.ProfileBoundary,
            Extension.Key,
            Extension.SchemaFingerprint,
            Channel.SchemaId,
            Channel.EntrySize,
            Channel.MaximumEntryCount,
            Channel.MaximumByteCount,
            Channel.Kind,
            Channel.PublicationPhase,
        });
    }

    FRegisteredExtension& Registered = Extensions.Add(Extension.Key);
    Registered.Id = Extension.Id;
    Registered.Key = Extension.Key;
    Registered.Revision = Extension.Revision;
    Registered.SchemaFingerprint = Extension.SchemaFingerprint;
    Registered.SchemaIds.Reserve(Pending.Num());
    for (FRegisteredChannel& Channel : Pending)
    {
        Registered.SchemaIds.Add(Channel.SchemaId);
        Channels.Add(Channel.SchemaId, MoveTemp(Channel));
    }
    return true;
}

bool FUnifiedScriptDataPlaneExtensionRegistry::Unregister(
    uint64 ExtensionKey,
    uint64 SchemaFingerprint)
{
    check(IsInGameThread());
    FRegisteredExtension* Extension = Extensions.Find(ExtensionKey);
    if (Extension == nullptr || Extension->SchemaFingerprint != SchemaFingerprint)
    {
        return false;
    }
    for (uint64 SchemaId : Extension->SchemaIds)
    {
        Channels.Remove(SchemaId);
    }
    Extensions.Remove(ExtensionKey);
    return true;
}

bool FUnifiedScriptDataPlaneExtensionRegistry::ValidateRows(
    const FUnifiedScriptDataPlaneExtensionDescriptor& Extension,
    const FUnifiedScriptDataPlaneExtensionChannelDescriptor& Channel,
    EUnifiedScriptDataPlaneExtensionChannelKind ExpectedKind,
    EUnifiedScriptGameplayDataPhaseV4 CurrentPhase,
    uint32 EntrySize,
    const void* Entries,
    uint32 EntryCount,
    FString& OutError) const
{
    check(IsInGameThread());
    OutError.Reset();
    const FRegisteredExtension* RegisteredExtension = Extensions.Find(Extension.Key);
    const FRegisteredChannel* RegisteredChannel = Channels.Find(Channel.SchemaId);
    const bool bNativePhase =
        CurrentPhase != EUnifiedScriptGameplayDataPhaseV4::ManagedTick &&
        CurrentPhase != EUnifiedScriptGameplayDataPhaseV4::ShuttingDown;
    const uint64 ByteCount = static_cast<uint64>(EntrySize) * EntryCount;
    if (RegisteredExtension == nullptr || RegisteredChannel == nullptr ||
        RegisteredExtension->SchemaFingerprint != Extension.SchemaFingerprint ||
        RegisteredChannel->ExtensionKey != Extension.Key ||
        RegisteredChannel->ExtensionFingerprint != Extension.SchemaFingerprint ||
        RegisteredChannel->Kind != ExpectedKind || Channel.Kind != ExpectedKind ||
        RegisteredChannel->SchemaId != Channel.SchemaId ||
        RegisteredChannel->EntrySize != EntrySize || Channel.EntrySize != EntrySize ||
        RegisteredChannel->MaximumEntryCount != Channel.MaximumEntryCount ||
        RegisteredChannel->MaximumByteCount != Channel.MaximumByteCount ||
        RegisteredChannel->PublicationPhase != Channel.PublicationPhase ||
        EntryCount > Channel.MaximumEntryCount || ByteCount > Channel.MaximumByteCount ||
        (EntryCount > 0 && Entries == nullptr) || !bNativePhase)
    {
        OutError = TEXT(
            "The extension row publication is unregistered, incompatible, out of bounds, or in the managed phase.");
        return false;
    }
    return true;
}

void FUnifiedScriptDataPlaneExtensionRegistry::Reset()
{
    check(IsInGameThread());
    Channels.Reset();
    Extensions.Reset();
}
