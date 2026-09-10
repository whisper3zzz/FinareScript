#pragma once

#include "CoreMinimal.h"
#include "FinareGameplayDataPlane.h"

enum class EFinareDataPlaneExtensionChannelKind : uint8
{
    Snapshot = 1,
    Event = 2,
};
enum class EFinareDataPlaneExtensionPublicationPhase : uint8
{
    Native = 1,
};

struct FFinareDataPlaneExtensionChannelDescriptor
{
    const TCHAR* Name = nullptr;
    uint64 SchemaId = 0;
    uint32 EntrySize = 0;
    uint32 MaximumEntryCount = 0;
    uint64 MaximumByteCount = 0;
    EFinareDataPlaneExtensionChannelKind Kind =
        EFinareDataPlaneExtensionChannelKind::Snapshot;
    EFinareDataPlaneExtensionPublicationPhase PublicationPhase =
        EFinareDataPlaneExtensionPublicationPhase::Native;
    const TCHAR* ProfileBoundary = nullptr;
};

struct FFinareDataPlaneExtensionDescriptor
{
    const TCHAR* Id = nullptr;
    uint64 Key = 0;
    uint32 Revision = 0;
    uint32 RequiredAbiVersion = 0;
    uint64 RequiredLayoutCompatibilityId = 0;
    uint64 SchemaFingerprint = 0;
    const FFinareDataPlaneExtensionChannelDescriptor* Channels = nullptr;
    uint32 ChannelCount = 0;
};

/**
 * Per-World registry for generated data-plane extensions. Descriptors are
 * copied at registration so module unload cannot leave borrowed metadata.
 */
class FINARERUNTIME_API FFinareDataPlaneExtensionRegistry final
{
public:
    bool Register(
        const FFinareDataPlaneExtensionDescriptor& Extension,
        FString& OutError);

    bool Unregister(uint64 ExtensionKey, uint64 SchemaFingerprint);

    bool ValidateRows(
        const FFinareDataPlaneExtensionDescriptor& Extension,
        const FFinareDataPlaneExtensionChannelDescriptor& Channel,
        EFinareDataPlaneExtensionChannelKind ExpectedKind,
        EFinareGameplayDataPhaseV4 CurrentPhase,
        uint32 EntrySize,
        const void* Entries,
        uint32 EntryCount,
        FString& OutError) const;

    void Reset();

    int32 GetExtensionCount() const { return Extensions.Num(); }
    int32 GetChannelCount() const { return Channels.Num(); }

private:
    struct FRegisteredExtension
    {
        FString Id;
        uint64 Key = 0;
        uint32 Revision = 0;
        uint64 SchemaFingerprint = 0;
        TArray<uint64> SchemaIds;
    };

    struct FRegisteredChannel
    {
        FString Name;
        FString ProfileBoundary;
        uint64 ExtensionKey = 0;
        uint64 ExtensionFingerprint = 0;
        uint64 SchemaId = 0;
        uint32 EntrySize = 0;
        uint32 MaximumEntryCount = 0;
        uint64 MaximumByteCount = 0;
        EFinareDataPlaneExtensionChannelKind Kind =
            EFinareDataPlaneExtensionChannelKind::Snapshot;
        EFinareDataPlaneExtensionPublicationPhase PublicationPhase =
            EFinareDataPlaneExtensionPublicationPhase::Native;
    };

    TMap<uint64, FRegisteredExtension> Extensions;
    TMap<uint64, FRegisteredChannel> Channels;
};
