# 最近修改说明（2026-11-05）

> 第一～六节对应提交 `744730b 更新 技能说明`（30 个文件，约 +7900 行）；第〇节是之后的运行时代码执行方案与其模式化合并；最前面两节是之后的技能契约收敛、仅 JS 运行时与文档精炼（尚未提交）。本文件只记录改动，不是完整项目文档；完整用法见 `SKILL.md` 与 `README.md`。

## 实机联调：技能全流程验证 + 丰富当前场景

- 服务在线：`localhost:8000`，Unity `2021.3.45f2c1`，工具 54，`max_details=32`；`~/.unity-mcp/skill.ini` 已无 `script_language`，上一轮「仅 JS」改动生效。
- 命令全通：`discover` / `groups` / `list`（tools 54、prompts 2、resources 3）/ `find` / `schemas`（一次 8 个）/ `schema` / `details --tag`（3 项）与 `details --members`（混合 2 项）/ `prompt` / `resource` / `request groups`（Git Bash 裸路由）/ `call` / `calls` / `console_read`。旧 MCP 的 `prompts/get`、`resources/read` 由 `prompt` / `resource` 走通。控制台 6 条日志，无 error / exception。
- JS 库 API 实机通过：`require(...)`、`workflowContext`（记录调用、成功数与耗时）、`callSkillJson`、`callMany`（两批 20+17 步，全部 `exit_code=0`）、`callSkillText`。这是 Python 客户端移除后首次用 JS 库 API 完成场景写入。
- 上一轮 C# 契约修复实机生效：`hierarchy_create` / `edit_gameobject` 的 `position`、`rotation`、`scale` 均标注「相对父对象的局部坐标 / 局部欧拉角 / 局部缩放」；`object_delete.confirm` 改为「显式授权」；`code_runner.mode` 默认 `none`。`edit_gameobject` **只传 `scale`** 已生效（读回 `[9.0, 2.2, 2.5]`），typo 修复确认。`code_runner` 的 `probe` / `execute mode=runtime` 可用（编译约 0.5 秒、142 个引用、无需 DLL）。
- 当前场景 `Assets/CannonBirdDemo/CannonBird.unity`：对象 60 → **110**。新增 `CannonScene/Details` 下 37 个（3 个子分组、石块/草丛/踏石/靶子、炮口环/铆钉/弹药箱与炮弹/旗、远山与云瓣），runtime 新建 7 个 `Detail_*.mat`、给 33 个渲染器赋材质、为 3 只鸟加 6 只眼睛 + 6 个瞳孔、加 1 盏暖色点光源；随后按截图把远山、云瓣、靶子移入相机可见区并成组。场景已保存，生成 `Preview_Game.png`（1280×720）与 `Preview_Detailed.png`（SceneView）。
- 幂等验证脚本 `demo/skill_scene_detail.js`（工程根、非 Assets，Unity 不导入；先发现再调用，重跑只补差异，可留可删）。技能文档 `SKILL.md` 未改，仍只写通用契约，不写具体项目经验。

---

## 本轮：SKILL.md 按 caveman 文言精炼

- 只改 `unity-package/Skills/unity3d-skill/SKILL.md`，未动 README、docs、Unity 源码与客户端脚本。
- 全篇改短句、去浮辞与重复修饰（约 16.5 KB → 14.7 KB，说明正文压缩约 13%）。代码块 7 处共 38 行、CLI/JS 示例、API 对照表、路径、命令、数字、错误码、退出码、URI 结构一律原样保留；所有否定约束（禁全集、不重试、不截断、不回退、不自动分批、不绕过、授权要求）未删。
- 顺手修掉上一轮遗留的措辞：「自定义 INI 也须先读取以选择解释器」→「自定义 INI 亦须先读取以校验连接」。运行时已只有 JS，不应再出现「选择解释器」。
- 验证：`node --test tests/test_unity_mcp.js` 64/64；`git diff --check`；代码块逐行比对无缺失；关键约束词全部在；`dist/unity3d-skill.zip` 重建后 10 项、无 `.py`、包内 SKILL.md 与源文件字节一致、`testzip()=None`。

---

## 最新状态：技能只写通用契约、运行时只有 JS（覆盖以下历史章节）

> 本文件后续章节记录当时实现；**与本节冲突处以本节为准**。

**1) 技能文档不再描述任何具体功能**

- `SKILL.md` 删除了代码执行、引擎操作、动画状态机、UI Toolkit、相机、特效、测试、建模、可选包等全部具体工具章节，示例中的真实工具名一律换成 `NAME_FROM_LIST` / `TAG_FROM_GROUPS` 等占位符；库示例用 `pickFrom(catalog, schema)` 伪代码表达「由调用方决定工具与参数」。
- 新增「铁律：先发现，再使用」：`discover` → `groups` / `list` / `find` → `details` / `schema` / `schemas` → `call` / `calls`；工具名、URI、tag、参数与枚举值只能来自本次服务返回，文档出现过的名字不代表可用；不可用、未安装、未知时如实告知并停止，不猜名、不重试、不用 `request` 绕过。参数语义只以本次 `schema` 为准。
- 原因：工具是否注册取决于 Unity 版本、工程、已安装包与启动配置，写死在技能里的能力说明会误导 Agent 直接调用不存在的工具。
- `README.md` 删除三节具体工具教程，改为 "Tool use is discovery-driven"；`README.zh-CN.md` 技能章节去掉具体工具清单并加全文声明；`docs/index.html` 的技能区改为「发现优先，不预设能力」，命令示例占位化，应用示例卡与工具分类区标注「以 discover/list 现场发现为准」。

**2) 剔除 Python，只留 JS**

| 位置 | 改动 |
| --- | --- |
| 技能包 | 删除 `scripts/unity_mcp.py`(+`.meta`)、`tests/test_unity_mcp.py`(+`.meta`)；`SKILL.md` / README / docs 只保留 `require('./scripts/unity_mcp.js')` 与 `node --test` |
| `McpLocalSettings.cs` | 删除 `_scriptLanguage` 字段、`ScriptLanguage` 属性、`NormalizeScriptLanguage()` 及 `OnEnable` 中的归一化调用 |
| `McpDiscovery.cs` | `McpConnectionInfo` 删除 `ScriptLanguage` 属性与 `scriptLanguage` 构造参数；`ToIni()` / `ToJson()` 不再写 `script_language`；`PublishConnectionInfo` 改用 `maxDetails:` 命名参数 |
| `McpServiceStatusWindow.cs` | 删除语言下拉框 `DrawScriptLanguageConfiguration()` 及调用；「复制技能安装提示词」只提 `SKILL.md` + `unity_mcp.js` + Node.js 18+，并说明 INI 只发布 `max_details` 等启动配置 |
| INI | 服务启动时不再写 `script_language`；客户端只读 `base_url` / `port` / `max_details`，旧 INI 残留字段被忽略（测试用例已覆盖） |
| 测试 | `McpDiscoveryTests` 改为断言 INI/discovery **不含** `script_language`；JS 用例改为「旧 INI 的 script_language 被忽略」 |

- 保留项：Unity 侧的 `python_runner` 工具是 MCP 能力（在编辑器里执行 Python 脚本），与技能客户端语言无关，**未删除**；`McpService.cs` 的 `.py` MIME 处理同理。
- `dist/unity3d-skill.zip` 重建：14 项 → **10 项**，包内无 `.py`，`SKILL.md` / `unity_mcp.js` / `test_unity_mcp.js` 与源文件字节一致，`testzip()=None`。

**3) 验证**

- `node --test tests/test_unity_mcp.js`：**64/64 通过**；`git diff --check` 通过；4 个改动过的 C# 文件词法括号未闭合 0 / 错配 0；`docs/index.html` 9 类标签开闭计数全等。
- 实机联调未做：当前 `localhost:8000/discovery` 返回 502，Unity 服务不在线。需重启 Unity（重新编译 C#）后再验：状态窗口无语言下拉、INI 不含 `script_language`、`schema hierarchy_create` 等描述为局部坐标/完整层级路径、`object_delete confirm` 为显式授权语义。重启前 `~/.unity-mcp/skill.ini` 仍残留旧 `script_language=js`，客户端会忽略，不影响使用。

---

## 一句话

把 Unity Skilled MCP 从「能按需发现」补齐到「能检索、能读正文、能批量执行」，补齐 7 个 Unity 侧专用工具、修复 Unity Console 反馈的编译错误；随后加入不生成 DLL 的运行时 C# 内存编译执行，并**整合进 `code_runner` 由 `mode` 显式选择，默认不编译**；最新一轮把两个客户端脚本改造成可 import 的库（原生参数 + 工作流上下文），并根治了 Git Bash 相对路由转换问题。

---

## 历史一轮：双语言脚本可作为库 import，并修复 Git Bash 相对路由（现已改为仅 JS）

**问题 1（修复）**：上一轮实测发现 Windows Git Bash / MSYS 会把 `request /groups` 的 `/groups` 自动转成 `C:/…/PortableGit/groups`，导致 URL 校验失败。原来只在 `SKILL.md` 写了规避说明，本轮在代码里根治：

- `unity_mcp.js` / `unity_mcp.py` 新增 `normalizeRequestTarget` / `normalize_request_target`。按环境变量 `EXEPATH`（指向 `<Git 根>/bin`）精确剥离根前缀，把转换后的本地路径还原成 `/ROUTE`；无法还原的盘符路径明确报错并给出提示，绝不当作 URL 访问。
- 同时支持**省略前导斜杠的裸路由**（`request groups`、`request capabilities/details`），从根上避开 shell 转换；绝对 http/https URL 与 CMD / PowerShell / POSIX shell 行为不变。
- 保留既有安全校验：`//host`、`file://`、凭据、片段、空白、控制字符、反斜杠仍被拒绝；路由规范化在 discovery 之前完成，非法输入零网络请求。

**问题 2（新增能力）**：两个脚本原本只能当 CLI 用（「传入初始信息」）。按参考用法（`unity_skills.workflow_context(...)` + `unity_skills.call_skill(name, **kwargs)`）把它们改造成**可被其他代码引用的库**：

- JS：`require.main === module` 保护 CLI，导出 CommonJS；`SIGINT` 处理也只在本文件作为入口时注册。
- Python：沿用 `__main__` 守卫，模块级新增函数与类。
- **零重构复用**：所有库方法把原生参数序列化进内部 `args`，直接调用原 `execute()`，因此与 CLI 共享 INI/discovery 校验、`max_details`、64 KiB 上限、目录白名单、不重试和错误码，不存在两套实现。仅把三处 stderr 打印换成 `_log_info` / `logInfo`（用 `contextvars` / `AsyncLocalStorage` 控制），库模式默认静默，CLI 输出文本完全不变。
- API：`Client` 类（`discover / groups / list / find / details / schema / schemas / call / calls / prompt / resource / request`）+ 模块级默认实例与顶层函数代理 + `call_skill` / `callSkill`（返回 MCP `result`）、`call_tool`、`*_text` / `*_json`、`result_text` / `result_json`。JS 方法全为 `async`；Python `call_skill(tool_name, /, **arguments)` 支持任意工具参数关键字。
- `workflow_context` / `workflowContext`：命名工作流上下文，块内调用自动记入 `wf.calls`（参数、结果或错误码、耗时），`wf.summary()` 汇总；只记录，不重试、不回滚、不自动重放，失败照常抛出。
- 参数序列化拒绝 `NaN` / `Infinity`、循环引用；`resource` 仍强制目录白名单，`request` 绝对外发地址仍需授权。

**文档与验证**：`SKILL.md` 新增「作为代码库调用」章节（含 Python/JS 示例与 CLI↔函数对照表）并改写 Git Bash 段落；`README.md` / `README.zh-CN.md` / `docs/index.html` 同步；`dist/unity3d-skill.zip` 重建。新增 JS 7 个、Python 6 个用例；`node --test` **64/64**、`unittest` **47/47**；真机（`localhost:8000`，Unity 2021.3.45f2c1，54 工具）双语言库 API 与工作流联调通过，失败调用也能正确记录。

---

## 〇、上一轮：runtime_code 合并进 code_runner，模式化选择

参考 `D:/WorkSpace/tdcloudshowproject_develop/U3DProject/Assets/Editor/ToolBox/RuntimeDebugTool.cs`（CodeDOM `CSharpCodeProvider` + `GenerateInMemory=true`）。上一轮先做成独立工具 `runtime_code`；本轮按用户要求合并为单一入口 `code_runner`，**支持模式选择，不选默认就不走编译**。

| `mode` | 行为 | `code` 输入 |
| --- | --- | --- |
| `none`（默认 / 不传） | **不编译、不执行任何代码**，返回 `compiled=false`、`executed=false` 与可用模式说明 | 忽略 |
| `runtime` | CodeDOM `GenerateInMemory` 内存编译，不写 `.cs`、不产出工程内 DLL、不触发 Unity 编译管线与域重载 | C# 语句片段，可 `return expr;` |
| `dll` | 原 `AssemblyBuilder` 流程：写临时 `.cs` → 编译 DLL → 加载 | 完整类 / 命名空间 |

- `action` 扩为 `execute` / `validate` / `probe`；只有 `mode=runtime` 或 `mode=dll` 才进入编译。`probe` 在 `mode=runtime` 时做一次 `return 1;` 真实最小编译，`mode=none` / `dll` 只做类型解析、`compile_check_skipped=true`，严格遵守「默认不编译」。
- `mode` 接受别名（`memory` / `in_memory` / `codedom` → `runtime`；`assembly` / `legacy` / `assemblybuilder` → `dll`；`off` / `no_compile` → `none`），未知值直接报错且不编译。
- runtime 模式参数：`code`、`includes`（复用为追加命名空间）、`assembly_patterns`、`capture_logs`、`max_log_chars`、`include_source`；响应统一带 `mode` 与 `fallback_mode=dll`。dll 模式参数与旧行为完全一致，**旧 MCP 协议流程不受影响**。
- 删除独立工具 `RuntimeCode.cs` 及其 `.meta`（能力已迁入 `CodeRunner.cs`）；保留 `RuntimeCodeExecutor.cs` 作为引擎：反射解析 CodeDOM 类型避免 asmdef 硬依赖、包装源码与行号映射、程序集名正则收集引用、`Application.logMessageReceived` 捕获日志（200 条 / 8000 字符）、异常解包、`PathToAssembly` 字节降级。引擎内的回退提示同步改为 `code_runner 的 mode=dll`。

**调用示例**

```sh
node scripts/unity_mcp.js call code_runner --args '{"action":"probe","mode":"runtime"}'
node scripts/unity_mcp.js call code_runner --args '{"action":"execute","mode":"runtime","code":"return 1+1;"}'
node scripts/unity_mcp.js call code_runner --args '{"action":"execute","mode":"dll","code":"..."}'
```

**边界**：任意代码在 Editor 主线程同步执行，无超时、沙箱、事务与回滚；编译冷启动可能超过客户端默认 15 秒，用全局 `--timeout 60`。CodeDOM 不可用时明确报错并指引 `mode=dll`，不自动降级、不重试。

**文档同步**：`SKILL.md` 章节改为「代码执行（code_runner）」并更新 frontmatter；`README.md` 英文小节改为模式表；`README.zh-CN.md` 工具清单；`docs/index.html` 命令示例、说明卡片与工具分类；`dist/unity3d-skill.zip` 重建。

**实机测试（重启服务后，`E:\unity3d-skilled-mcp\demo`，Unity 2021.3.45f2c1，端口 8000，54 个工具）**：全部通过。

- `code_runner` 默认不传 `mode` → `compiled=false` / `executed=false`；`probe --mode=none` → `compile_check_skipped=true`；未知 `mode=bogus` → 拒绝且不编译。
- `probe --mode=runtime` → `available=true`、`compile_check=true`（0.96 秒）、143 个程序集引用；**Mono CodeDOM 在该版本可用，Roslyn 备用引擎暂不需要**。
- `validate` 只编译不执行；`execute "return 1+1;"` → `return_value=2`；`Debug.Log` 片段 → `logs="[Log] skill-smoke"`、`log_count=1`；编译错误还原为「片段行 1:20 CS0103」。
- `mode=dll` 空 `code` → 旧分支返回 `code parameter is required`，未触发编译，旧流程入口仍在。
- 发现链路：`groups` / `list` / `find` / `details --tag` / `details --members` / `schema` / `schemas` / `prompt` / `resource` / `call` / `calls`（失败即停 `stopped_at=1`）全部正常；JS 与 Python 双语言一致；全集 `all/*` 与未知工具按预期拒绝。
- 环境坑：Windows Git Bash 会把 `request /ROUTE` 的 `/ROUTE` 转换成本地路径导致 URL 校验失败；当轮先用绝对 URL 或 `MSYS2_ARG_CONV_EXCL='*'` 规避并在 `SKILL.md` 补充说明。（**该问题已在最新一轮从代码层修复**：按 `EXEPATH` 自动还原 + 支持裸路由，见文首「本轮最新」一节。）

**待确认**：其他 Unity 版本 / .NET profile 下 CodeDOM 的可用性（本轮只在 2021.3.45f2c1 验证）。

## 一、最新一轮：技能客户端扩展

对照 `com.coplaydev.unity-mcp` 复核后确认，工具覆盖已基本齐全（其 `batch_execute` 由本包服务端现有 `batch_call` 承担），真正缺口是技能「看得到定义却用不了」。**本轮未新增任何 C# 文件**，只改双语言客户端。

| 新命令 | 作用 | 约束 |
| --- | --- | --- |
| `find QUERY [--kind tools\|prompts\|resources]` | 目录摘要按名称/URI/说明/标签本地过滤 | 只取一次目录，不取详情、不走 MCP 协议；关键字 1-128 字符 |
| `prompt NAME` | 校验目录后 `prompts/get` 取模板正文 | 名称必须来自 `list --kind prompts`；响应须含 `messages` |
| `resource URI` | 校验目录后 `resources/read` 取资源内容 | **只接受已注册 URI**；响应须含 `contents` |
| `calls --calls-file steps.json` | 顺序执行多个 `tools/call` | 失败即停、保留已执行步骤、`exit_code` 作退出码 |

安全设计：

- 服务端 `resources/read` 兼容任意 `http://` / `file://` URI，直接暴露会绕过 `request` 的外发授权，因此 `resource` 强制先比对本服务 `/resources` 目录。
- `calls` 复用 `max_details`（默认 `32`，`0` 不限）与 65536 字节请求体上限；无事务回滚，失败后须先核实编辑器状态、只重放未执行步骤。
- 名称 / URI / 关键字的格式校验统一提前到 `discover()` 之前，非法输入零网络请求。
- 抽出 `rpcEnvelope` / `rpcResult` / `rpcCall` / `directory` 公共层，`call` 改为复用；`prompt` / `resource` 只在用户显式调用时才走原协议，不自动调用 `tools/list`、`prompts/list`、`resources/list`。

改动文件：`scripts/unity_mcp.js`、`scripts/unity_mcp.py`、`tests/test_unity_mcp.js`、`tests/test_unity_mcp.py`、`SKILL.md`。

## 二、同一阶段的服务端与工具补齐

### 1. HTTP 渐进式发现 + 批量 Schema

- `McpDiscovery.cs` 新增 `POST /capabilities/schemas`（别名 `/mcp/capabilities/schemas`），请求 `{tools:[...]}`，返回标准 MCP 工具定义数组；`McpConnectionInfo.ToJson()` 增加 `schemas_url`。
- 规则与详情接口一致：去重保序、`max_details` 上限、64 KiB 响应/请求体上限、拒绝 `all`/`*` 与全集选择、未知或禁用工具 404；`InvalidSelector` 补上控制字符校验。
- `McpDiscoveryTests.cs` 新增 `TestBatchSchemas` 自检用例。
- INI 与 `/discovery` **仍不发布 `mcp_url`**，防止 Agent 绕过技能直连 MCP；原 JSON-RPC / MCP 兼容链路完全保留。

### 2. 引擎操作 `engine_control`

17 个 action，覆盖上下文与选择集（`get_info` / `get_selection` / `set_selection` / `set_active_tool` / `undo` / `redo` / `step`）、构建（场景列表、切平台、排队 `build_player`、`get_build_status`）、工程设置、编译诊断与重编译。构建默认排队返回 `job_id`，播放模式拒绝构建与切平台；`get_compilation` 增加 `diagnostics_available` 标记。

### 3. 新增 7 个 Unity 侧工具 + 1 处补洞

| 工具 | 覆盖 | 依赖 |
| --- | --- | --- |
| `edit_animator` | Animator Controller 结构、过渡条件、1D 混合树、Play 模式驱动（20 action） | 内置 `UnityEditor.Animations` |
| `ui_toolkit` | UXML/USS 文本读写、PanelSettings、UIDocument、截图（16 action） | 内置 UI Toolkit |
| `manage_camera` | 相机创建/设置/离屏截图/多视角；Cinemachine CM2/CM3 兼容（11 action） | Cinemachine 可选 |
| `edit_effects` | LineRenderer / TrailRenderer；VFX Graph 参数、事件、播放控制（12 action） | VFX Graph 可选 |
| `run_tests` | Test Runner 排队执行、状态轮询、卡住复位（3 action） | Test Framework 可选 |
| `edit_probuilder` | 形状创建、网格信息、挤出/删面/平滑（6 action） | ProBuilder 可选 |
| `OptionalApi.cs` | 反射层：类型查找、重载匹配、字段/属性读写、枚举转换 | 无（避免 asmdef 硬依赖） |

- 可选包全部走运行时反射，未安装只让对应工具返回「未安装」错误，不影响其他工具。
- `project_operate` 补 `delete`（默认移入回收站，`force=true` 永久删除，拒绝 Assets 根）并修复 `refresh` / `reload` 只有枚举、没有状态树分支的旧 bug。

### 4. Unity 真实编译错误修复（依据用户 Console 报错）

| 文件 | 问题 | 处理 |
| --- | --- | --- |
| `EditAnimator.cs` | `AddState(Motion, Vector3)` 不存在 | `AddState(name, position)` 后赋 `state.motion` |
| `EditEffects.cs` | `FindObjectsOfType(Type)` 返回 `UnityEngine.Object[]` | 循环加花括号 + `as Component` 判空（同时修 CS1023/CS0103） |
| `RunTests.cs` | `DispatchProxy.Create(Type, Type)` 不存在 | 反射遍历静态重载，优先泛型 `Create<T,TProxy>()`，移除 `Reflection.Emit` 方案 |
| `EngineControl.cs` | `CompilationPipeline.assemblyCompilationErrors` 不存在 | 反射挂 `assemblyCompilationFinished`，失败静默降级并标记 `diagnostics_available` |
| `EngineControl.cs` | 命名空间 `UniMcp.Tools` 遮蔽 `UnityEditor.Tools` | 全部改完全限定 `UnityEditor.Tools.*` / `UnityEditor.Tool` |

## 三、文档与分发

- `SKILL.md`：新增「目录检索与内容读取」「批量工具调用」两节，更新 description、退出码说明与外发授权提示。
- `README.md` / `README.zh-CN.md`：命令示例、技能能力说明、约束补充。
- `docs/index.html`：命令块、新增「检索、正文与批量」卡片，Resources/Prompts 使用方式补技能命令，修正过时的「SKILL.md 约 5 KB」文案。
- `dist/unity3d-skill.zip`：重建为 14 项，包内 `SKILL.md` 与源文件字节一致。

## 四、验证结果

| 项目 | 结果 |
| --- | --- |
| `node --test tests/test_unity_mcp.js` | 57 / 57 通过（本轮 +9） |
| `python -B -m unittest discover -s tests -p 'test_*.py'` | 41 / 41 通过（本轮 +8） |
| `git diff --check` | 通过 |
| C# 词法括号配对、action/leaf/handler 一致性 | 全部通过 |
| `docs/index.html` 标签配对（div/section/ul/li/pre/code） | 全部相等 |
| ZIP 完整性 | `testzip()` 返回 `None`，14 项 |

**注意**：本地环境无法调用 Unity 编译器，上述 C# 只做了静态词法/结构检查，**真实编译结果以 Unity Console 为准**。

## 五、待确认与下一步

1. 请确认 7 个新增 C# 工具在 Unity Console 的实际编译结果；如有新报错按第四节方式继续修。
2. 可选包（Cinemachine / VFX Graph / ProBuilder / Test Framework）需在装了对应包的项目里实测；`demo` 未装这些包，会走「未安装」分支，属预期。
3. 可继续扩展：UGUI 控件创建工具、结果落盘 `--out`、把服务端 `batch_call` 封装成技能级原子批量命令。

## 六、需要保持的设计约定

- `~/.unity-mcp/skill.ini` 与 `/discovery` **不得出现 `mcp_url`**；Agent 只能通过技能脚本访问服务。
- 详情 / 批量 Schema 数量不硬编码，由 Unity 启动配置 `max_details` 决定，`0` 为不限。
- 禁止一次拉取能力全集；组超限或全集选择返回 400，客户端不拆分、不重试、不静默截断。
- 旧 MCP 客户端的 `tools/list`、`prompts/list`、`resources/list`、`prompts/get`、`resources/read` 行为保持不变。
- 用户可见品牌统一为 **Unity Skilled MCP**；技术路径、命名空间、协议名、图片文件名不改。
