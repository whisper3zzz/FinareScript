#pragma once

#include "UnifiedScriptNativeTransport.h"

// Shared module-internal codecs for forward calls and reverse callback frames.
namespace UE::UnifiedScript::NativeTransport::Private
{
bool DecodeValueV3(const FUnifiedScriptNativeValueV3&, const FUnifiedScriptNativeCallFrameV3&,
    uint32 EntryByteCount, FUnifiedScriptValue&, uint32 Depth);
bool EncodeValueV3(const FUnifiedScriptValue&, FUnifiedScriptNativeCallFrameV3&,
    FUnifiedScriptNativeValueV3&, uint32& ByteCursor, uint32 Depth);
}
