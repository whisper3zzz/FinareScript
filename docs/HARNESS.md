# Engineering Harness

Harness 是开发者和 Agent 共用的执行框架，不是新的 gameplay runtime。
它把任务边界、源码导航、验证选择和证据串在一起，复用已有 CI lanes。
产品顺序仍由 [ROADMAP.md](ROADMAP.md) 的 Current execution cursor 决定。

按行为选择托管测试、Python 工具测试和原生集成检查，先看
[TESTING.md](TESTING.md)。当前能力边界见 [CAPABILITIES.md](CAPABILITIES.md)，
历史计数和验收记录见 [历史索引](history/README.md)。

## 使用原则：工具可选，必要验证不能省

AI 的执行约束以 [AGENTS.md](../AGENTS.md) 为准。Harness 提供能力，不规定每轮
必须走一遍 `status -> plan -> run`。清楚上下文时直接实现，知道相关测试时直接运行。
先按实际行为变化判断风险，再选择命令；不要为了使用框架扩大工作量。

Harness 的 AI 编排采用单架构师、单执行者：项目主会话由 GPT-6 Astra `xhigh`
负责跨模块设计、Roadmap 切片和最终集成；`executor` 由 GPT-5.6 Luna `xhigh`
负责边界明确的代码实现、生成和针对性验证。配置入口是
[`../.codex/config.toml`](../.codex/config.toml) 和
[`../.codex/agents/executor.toml`](../.codex/agents/executor.toml)。同一工作树不并行
启动多个写入 Agent；状态说明、只读判断和微小规则修改不为分工而强制委派。

文档改动检查文档，局部逻辑改动测试对应行为，ABI/生命周期改动测试受影响的链路。
完整 profile 适用于需要其覆盖范围的任务或 milestone/发布收尾，不是普通改动的必选项。
例如修改 Harness 的输出文案，可以只测输出行为；修改 AI 约束不需要 .NET/UE 验收。

## 日常入口

在仓库根目录运行（Python 3.10+，仅标准库）：

```powershell
# 不清楚状态时查询
python -B tools/harness.py status
# 不确定影响面时参考，推荐不是必跑清单
python -B tools/harness.py plan --paths src/Finare.Core/ScriptAssetModel.cs
python -B tools/harness.py plan --base-ref HEAD~1
# 确实需要组合验证时选 profile
python -B tools/harness.py run --profile quick
python -B tools/harness.py run --profile precommit --engine-root D:\UE_5.8
```

- `status`：读取 Git、Roadmap 游标、未完成任务计划和最新 Harness 报告；
  不执行测试、不改文件。
- `plan`：合并 staged/unstaged/untracked 改动，或只看显式 `--paths`；
  `--base-ref` 额外纳入该 Git ref 到 HEAD 的提交差异。输出推荐 gates、原因、
  前置依赖和命令。未知路径保守推荐 `precommit`；这是候选影响面，不是必须执行的清单。
  路径算法不能判断文案变化与行为变化，允许在分析实际 diff 后改选或直接跑相关测试。
- `run`：只执行显式选择的 profile（默认 `quick`），不自动执行 plan 推荐的重型
  测试。按依赖串行执行，每次分配全新目录；默认遇失败停止，剩余标记 `not-run`。
- 各命令支持 `--json`。`plan` 不要求本机装好重型工具，真正 `run` 先检查所有依赖。
- 引擎依次取 `--engine-root`、`FINARE_UE_ROOT`、本地配置。
  比较工程只能通过 `--comparison-root` 或 `FINARE_COMPARISON_ROOT` 指定；
  performance 会操作该外部目录，执行前必须确认它在用户授权的项目边界内。

`harness.local.json` 被 Git 忽略，可参考
[`harness/local.example.json`](../harness/local.example.json)。它记录引擎路径和
`protectedPaths`，不应包含密钥。受保护文件绝不能被 Agent 改写/暂存；Harness
会核对运行前后内容。存在脏的受保护文件时，会提前拒绝可能重新生成源码的
Golden Path/performance gate，而不是自行恢复用户文件。

## 验证层级

| Profile | 含依赖的验收范围 | 不代表什么 |
| --- | --- | --- |
| `quick` | Python 工具自测（Harness、文档检查、托管测试分类）、仓库结构/契约不变量 | 不是 .NET 编译或 UE 运行 |
| `managed` | quick + 全量 Debug .NET 测试 | 不是原生 ABI 运行证据 |
| `precommit` | managed + 确定性生成 | 名字不触发 Git commit；不是打包验收 |
| `native` | precommit + UE Editor 构建 + CMD Automation | Editor 构建不等于 Game/打包通过 |
| `script-class` | native + 完整 Script Class Golden Path | 不代表任意 Puerts 项目迁移完成 |
| `performance` | native + 显式比较工程的双端 workload | 不是所有场景都更快的结论 |

规则定义在 [`harness/manifest.json`](../harness/manifest.json)，不能在工具里再维护
一份 gate 清单。共享依赖只运行一次。新增 gate 必须声明超时、工具依赖、源码
写入风险、底层命令和报告契约。可通过 `--timeout-seconds` 临时缩短所有 gate 的
超时做诊断；超时/中断只终止 Harness 启动的进程树，不杀其他 UE 进程。

`--require-clean` 可用于严格验证，拒绝任何 Git 脏状态。Harness 本身不提供发布
认证；完整发布仍用 [RELEASE_READINESS.md](RELEASE_READINESS.md) 的现有入口，
其 clean revision、SDK、平台、性能、package 独立 gate 不被缩减。
CI 的七条 lane 保留，轻量 CI 通过 `tools/run-tool-tests.py` 运行全部 Python 工具自测。

## 任务记录按需维护

普通任务在源码、测试和答复里完成交接即可，不额外建计划文件。
跨会话长任务、实质架构选择或用户要求时，才使用 [计划模板](plans/TEMPLATE.md)，
记录目标、重要决定和剩余工作，不抄写工具日志，也不重复维护多份状态。

只有产品能力或 milestone 验收有变化才更新 Roadmap 游标。用户临时指定的任务
不自动推进产品 milestone。失败要真实记录并修原因；不为凑齐计划勾选追加无关任务。

## 源码导航与所有权

| 领域 | 第一入口 | 契约/测试入口 |
| --- | --- | --- |
| 语义模型与稳定 ID | `src/Finare.Core/`、`src/Finare.Semantics/` | `docs/ARCHITECTURE.md`、`docs/IDENTITY_SIDECAR.md` |
| C# lowering / Declaration IR | `src/Finare.CSharp/CSharpScriptAssetLowerer.cs` | `docs/DECLARATION_IR.md`、`tests/Finare.Tests/CSharpScriptAssetLowererTests.cs` |
| Graph / 可视化与 Code 互转 | `src/Finare.Graph/`、`src/Finare.Authoring/` | `docs/AUTHORING_SURFACE.md`、`docs/AUTHORING_TRANSACTIONS.md` |
| CoreCLR / chunk / reload | `src/Finare.Runtime/` | `docs/COMPILED_CHUNKS.md`、`docs/HOT_RELOAD_RELIABILITY.md` |
| UE managed API / ABI | `src/Finare.Unreal/` | `docs/UNREAL_ADAPTER.md`、`tests/Finare.Tests/UnrealManagedFunctionDispatchTests.cs` |
| 原生桥接和 Blueprint Class | `HostProject/Plugins/Finare/Source/` | `FinareBlueprint.cpp`、`FinareBlueprintClassMaterializer.cpp` 及模块内 Automation |
| CLI / 安装 / 项目工作流 | `src/Finare.Cli/`、`templates/`、`tools/setup-unreal-project.ps1` | `docs/UNREAL_ONBOARDING.md`、`docs/UNREAL_SDK.md` |
| CI / package / 双端性能 | `tools/run-ci-lane.ps1`、`comparison/` | `docs/CI.md`、`docs/RELEASE_READINESS.md`、`docs/PERFORMANCE_WORKLOAD_SUITE.md` |

完整依赖限制见 [ARCHITECTURE.md](ARCHITECTURE.md) 和
[CONTRIBUTING.md](../CONTRIBUTING.md)。Harness 不复制这些长文档；只给入口。
生成文件的权威来源是 Contract/反射/生成器，`artifacts/` 和 `HostProject/Saved/`
是执行证据，不是新需求或源码真相。历史 Roadmap 计数只表示对应时间的结果。

## 报告、失败与防止旧证据冒充

每次运行产生 `artifacts/harness/runs/<unique-id>/report.json`、`summary.md`、
每个 gate 的日志和原有 provider 报告。报告包含完整 Git revision、脏文件列表、
源码指纹（HEAD、index/working diff 和 untracked 内容）、开始/结束状态、精确参数、
持续时间、退出码、报告 SHA-256、失败原因和未运行 gate。不会复制源码或凭证。

非零退出、缺少报告、错误 schema/status/lane、超时、中断、受保护文件变化，或
运行期间 Git 源码变化，都不能成为该次 profile 的通过结果。已有输出目录一律拒绝，
防止捡起旧文件；工具不会替你回滚。这是组合执行的报告规则，不要求所有日常验证
都通过 profile 执行。

`status` 对比最新报告和当前源码指纹；只有 `matches-source` 说明 Git 输入相符。
它不等于工具链、ignored 二进制或外部比较工程仍一致，更不是重新执行测试。
旧报告显示 `stale`；它是源码发生变化的提示，不是“重跑所有检查”的命令。
判断变化是否影响原测试：修改末尾说明文档无需重跑 .NET/UE；修改被测逻辑则需
重跑相关测试。可以说明原结果的适用范围，但不得把旧报告伪装为新运行。
即便新的 quick 是绿的，也不继承历史 UE/package 结果。

报告、源码指纹和哈希由工具自动记录。无需再建一份台账、备份或手工复核同一哈希。
日常交接以交付结果和重要限制为主，完整门禁计数与日志留在报告里。

一个 checkout 同时只允许一个 Harness run。锁是 `.cache/finare-harness.lock`；
手动启动的构建不在锁保护范围内。强制结束工具后，确认该 PID 已结束、所有子进程
都停止，再删除这个确切的锁文件。不要自动清理用户工作区。

Windows provider 用 `powershell.exe -NoProfile`，并只在子进程中移除继承的
`PSModulePath`，让 Windows PowerShell 5.1 自行构造标准模块路径。这避免从
PowerShell 7 经 Python 启动时误载 Core 版 Utility 模块、导致 `Get-FileHash`
消失。不会修改当前终端或系统环境变量；第三方自定义模块不属于这些 provider 的依赖。

框架自身的行为回归位于 `tools/tests/test_harness.py`，使用临时 Git 仓库和小型
子进程验证成功、失败、超时、报告缺失、来源过期、保护和依赖调度，不需要 UE。
