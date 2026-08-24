#pragma once

#include "SGraphNode.h"

class UUnifiedScriptEdGraphNode;

/** Blueprint-style card for a semantic Authoring Surface node. */
class SUnifiedScriptGraphNode final : public SGraphNode
{
public:
    SLATE_BEGIN_ARGS(SUnifiedScriptGraphNode) {}
    SLATE_END_ARGS()

    void Construct(const FArguments& InArgs, UUnifiedScriptEdGraphNode* InNode);

protected:
    virtual TSharedRef<SWidget> CreateNodeContentArea() override;
};
