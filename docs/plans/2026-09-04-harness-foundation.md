# 2026-09-04 — Engineering Harness foundation

Status: complete

## Goal and acceptance

按用户要求暂停产品功能扩展，先建立可以持续维护项目的 Harness。
单入口能展示状态、选择验证、执行现有 gates、生成可追溯报告；失败、跳过和旧
证据不会混为通过。M13.3C-1c-b 保持未完成，不推进 Roadmap 产品游标。

## Scope and ownership

- 新增 `AGENTS.md`、`harness/manifest.json`、本地配置示例、
  `tools/harness.py`、`tools/tests/test_harness.py`、Harness 文档和计划模板。
- 小范围接入 README、CONTRIBUTING、CI 文档/工作流、仓库 validator 和 gitignore。
- 本机 `harness.local.json` 保持 ignored；显式保护既有 DefaultEngine.ini、
  DefaultInput.ini、UnifiedScriptSyncAssetsCommandlet.cpp、UnifiedScriptGeneratedThunks.h。
- 不修改 product C#/C++、不执行外部 BlockBreaker/Lyra 任务、不重新设计 runtime，
  不清理已有 `tools/__pycache__/`，不自动 commit/push。

## Execution

- [x] 审计现有七条 CI lane、Golden Path、Roadmap 和未提交文件。
- [x] 冻结 Harness 的复用边界、profile 分级和报告来源规则。
- [x] 实现 read-only status/plan、严格 manifest、执行和失败报告。
- [x] 补齐临时仓库行为回归，接入 CI 和仓库校验。
- [x] 运行 quick 与 precommit，并实际验证状态过期和失败分支。
- [x] 核对受保护文件哈希、审查 diff、完成交接。

## Verification ledger

| Gate | Result | Evidence |
| --- | --- | --- |
| Harness behavior tests | passed | `artifacts/harness/runs/foundation-precommit-fixed/harness-tests.log`，38/38；另一个 malformed-provider 回归也已单独执行通过 |
| Repository validator | passed | 同次 `repository.log`，133/133 |
| Managed suite | passed | 同次 `managed-tests/ci-lane.json` 和 TRX，691/691 |
| Deterministic generation | passed | 同次 `deterministic-generation/generation/deterministic-generation.json`，9/9 |
| UE / package / performance | not-run | 本轮只修改工程执行层；不借用上轮验收结果 |

## Handoff

首轮完整运行真实暴露子进程环境问题，保留失败报告。已在 Harness 子进程边界
重置 Windows PowerShell 的模块搜索路径，并加入真实 Windows provider 回归；
不修改任何生成产物或降低比对标准。修复后完整 precommit 已通过。

证据来源为 `6c03d51` 加对应报告中的 dirty-source fingerprint，不声称是已提交
快照。以上 ledger 保留原有运行记录，不因后续文档修改补跑完整验证。
后续 `foundation-final` 运行已通过；最新 Harness 行为测试为 40/40。
超时回归改为在清理返回后向子进程发送探测信号，避免把 Windows 清理期间的
写入误判为清理后仍存活。完整行为测试已重新通过。

边界：路径路由是保守建议，不是完整依赖图；ignored 二进制、外部工程和工具链
不会仅因源码 fingerprint 相同而自动认证。CI 接入已提交为工作区代码但未触发远程
工作流。用户文件保持原样；本地提交由用户授权后的 Git 操作决定，Harness 不会
自行提交或推送。

后续产品顺序以当前 `docs/ROADMAP.md` 为准，不沿用此历史计划的游标。
`status`、`plan` 和 profiles 都是按需工具；遵循 `AGENTS.md` 的风险分级验证规则。
