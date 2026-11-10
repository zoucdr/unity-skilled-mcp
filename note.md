# 最近修改说明（2026-11-07）

> 名称注记：客户端脚本原名 `unity_mcp.js` / `unity_mcp.py`，测试原名 `test_unity_mcp.*`，已于 2026-11-08 统一更名为 `unity_bridge.*`。下文含历史章节均用新名，端口、INI 节名（`[unity3d-skilled-mcp]`）与协议未变。

> 最新一轮：**客户端脚本更名为 `unity_bridge.js` / `unity_bridge.py`**（连同 `tests/test_unity_bridge.*`），内容与契约不变，仅名称与引用同步；Python 模块名随之改为 `import unity_bridge`。上一轮：**补齐 `scripts/unity_bridge.py`，与 `unity_bridge.js` 同契约、同名函数；并在技能文档中禁止运行期阅读客户端源码**。两个客户端子命令、选项、退出码、输出形态一致，Python 端另收 snake_case 别名；`SKILL.md` 只用一小节说明「换成 `python scripts/unity_bridge.py` 即可」，不重复调用与参数篇幅。上一轮：**第三个门面改名 `get_tool`，并把实例标识挪到 `SessionState`，域重载不再触发重新发现**。`get_prompt` / `get_resource` / `get_tool` 三个兜底入口从此同名同构；脚本重编译、进出 Play Mode 不再更换 `instance_id`，客户端无需重新 `discover`、重取工具与 Schema，只有 Unity 进程重启才会漂移。任意 `tools/call` 完成后结论写入 `ProjectSettings/UnitySkilledMcpTasks.asset`，Unity 重启后同一 `request_id` 仍可查；超过等待阈值（默认 10s）先返回成功语义的 `{status:"running", task_id, query_path}`。文首表格为当前注册名，其后章节为历史记录，不追溯改写。早期描述的 `DefineTools` / `*.Tools.cs` / 状态树架构与旧工具名（`hierarchy_create` / `edit_gameobject` / `code_runner` 等）均已失效。本文件只记录改动，不是完整项目文档；完整用法见 `SKILL.md` 与 `README.md`。

## 本轮：客户端脚本更名为 unity_bridge

> 用户：「unity_mcp.js 和 py，修改名字为 unity_bridge」。

### 一、改名映射（内容未动）

| 旧名 | 新名 | 说明 |
| --- | --- | --- |
| `scripts/unity_mcp.js` | `scripts/unity_bridge.js` | `git mv`，`.meta` 的 guid 原样保留（`0f414e85…`） |
| `scripts/unity_mcp.py` | `scripts/unity_bridge.py` | 新增文件，`mv` 后重建 `.meta` |
| `tests/test_unity_mcp.js` | `tests/test_unity_bridge.js` | 测试随之改名，内部 `CLI` 路径同步 |
| `tests/test_unity_mcp.py` | `tests/test_unity_bridge.py` | 内部 `SCRIPT` 路径与 `import unity_bridge` 同步 |

不换概念，只换名字：端口、`[unity3d-skilled-mcp]` INI 节、`/discovery`、`/requests/{id}`、`X-Mcp-*` 头、`skill_dir` 语义、子命令与选项全部不变。Python 模块名随文件名变为 `import unity_bridge`；`contextvars` 键（`unity_bridge_quiet` / `unity_bridge_workflow`）与 `--version` 输出同步。

### 二、引用同步（14 个文件、219 处）

- 执行与注册：`unity_bridge.js` 用法串与库注释、`unity_bridge.py` 的 `prog` / `description` / 文档串。
- Unity 侧：`McpDiscovery.cs` 的 `skill_dir` 注释、`McpDiscoveryTests.cs` 的两条「客户端必须存在」断言、`McpServiceStatusWindow.cs` 的安装提示词。
- 文档：`SKILL.md`（铁律、连接、命令示例、Python 小节、库示例、回归测试命令）、`README.md`、`README.zh-CN.md`（含「两个等价实现」表述修正与目录树）、`docs/index.html`、`docs/ra2_allied_build.js`（示例客户端路径）、用户使用指南。

### 三、验证

- `node --test tests/test_unity_bridge.js`：**66/66**
- `python tests/test_unity_bridge.py`：**36/36**（含「与 JS 导出名一一对应」用例，改名后仍全绿）
- `python scripts/unity_bridge.py --help` / `--version` 正常，`usage: unity_bridge.py`
- Unity 2021.3.45f2c1：`AssetDatabase.Refresh` 后重编译 **0 error / 0 warning**；`McpDiscoveryTests.RunAll()` → 「服务发现自检通过」（含两条新名断言）
- 真机 `localhost:8000`：`discover`、`engine.recompile`、`engine.get_compilation`、`call`、`status` 全部经新脚本通过，`instance_id` 仍为 `7350a064…`（改名不影响绑定）

## 本轮：补充 Python 客户端 unity_bridge.py（与 JS 同契约）

> 用户：「ai 有时候更擅长写 py 脚本，目前 unity-skill-mcp 技能里面有 unity-mcp.js 我需要再实现一套 unity-mcp.py，并提供一样的函数名，在技能主网文档中说明。并禁止 ai 在运行时去读取这两个脚本文件，接口说明写到 Skill.md 尽量只是说明 unity-mcp.py 也能工作，不是和 unity-mcp.js 一样用大量篇幅说明调用和参数。」

### 一、新增 `scripts/unity_bridge.py`

| 维度 | 做法 |
| --- | --- |
| 依赖 | 仅标准库（`urllib`、`http.server` 测试用），Python 3.8+，无第三方包 |
| 子命令 | 与 JS 完全一致：`discover / groups / list / find / details / schema / schemas / call / prompt / resource / request / status` |
| 选项 | `--ini` `--timeout` 置于子命令前；`--expected-project` `--instance-id` `--verbose` 前后皆可；`call` 另有 `--request-id`；`details` 有 `--tag` 与 `--members[-file|-stdin]`；`request` 有 `--method` `--header` `--data[-file|-stdin]` |
| 退出码 | 0/2/3/4/5/6/130，与 JS 逐项相同；错误文案同源 |
| 函数名 | 25 个 JS 导出名全部同名保留（`Client` / `configure` / `getClient` / `connectionInfo` / `listCapabilities` / `callSkill` / `workflowContext` / `resultText` / `normalizeRequestTarget` / `DEFAULT_MAX_DETAILS` 等），另加 snake_case 别名（`connection_info` / `call_skill` / `workflow_context` …），两种写法都可调 |
| 实现路径 | CLI 与库共用唯一 `execute(args)`，无第二套校验；与 JS 一样「同一套 INI 读取 → discovery 校验 → 绑定 → 上限/白名单 → 单次请求」 |

关键移植点（照抄 JS 语义，避免两套行为）：

- `bind()`：错工程只警告（目录查询）或拒绝（`call` / `prompt` / `resource` / 同源 `request`）；实例漂移直接 `EXIT_PARAM` 且不重放。
- `--expected-project` 编码进 `X-Mcp-Expected-Project`，实例进 `X-Mcp-Instance-Id`；手工注入这两个头一律拒绝。
- 只在 `call` 自动生成 `params.request_id`；失败异常补 `request_id` / `instance_id` / `query_path` 三字段。
- `groups` 同时接受 v2 的 `namespaces` / `tags` 与旧 `groups` 数组；`details` / `schemas` 校验 `count == len(items)`、`max_details` 一致性与 64 KiB 上限。
- `normalize_request_target`：绝对 URL 原样、单个 `/` 开头的路由、经 `EXEPATH` 还原的 MSYS 路径、裸路由补 `/`；`file://` 等其它协议原样透传给 URL 校验拒绝。
- 每个 HTTP 操作走 `ProxyHandler({})` 的空代理 opener：**不读 `HTTP(S)_PROXY`**，与 JS 裸 socket 行为一致（否则本机请求可能被代理改写）。这是本轮实测发现并修掉的差异点。

### 二、文档

- `SKILL.md` 新增「Python 运行时（与 JS 等价）」四小节：只说明换前缀即得命令、何时选它、库模式函数名（含 snake_case）、校验同源；附 4 行 `import unity_bridge` 示例。不复制上文调用与参数说明。
- 「铁律」新增一条：**禁读 `scripts/unity_bridge.js`、`scripts/unity_bridge.py`、`tests/`**，接口以 `SKILL.md` 与 `--help` 为准。
- 连接章节改为「两个等价实现」；`README.md` / `README.zh-CN.md` / `docs/index.html` 的「唯一客户端」表述同步；状态窗口「复制技能安装提示词」补 Python 3.8+ 与子命令一致说明。
- `McpDiscoveryTests` 增加两条断言：技能目录必须同时存在 JS 与 Python 客户端（防止再出现某一实现被悄悄删掉）。

### 三、验证

- `python tests/test_unity_bridge.py`：**36/36 通过**（新增 660 行夹具，覆盖发现校验、绑定与漂移、`status`、上限与互斥、目录过滤、错误码、MSYS 路径还原、库 API 与 JS 导出名一一对应、CLI 三种 JSON 输入）。
- `node --test tests/test_unity_bridge.js`：**66/66** 不受影响；重编译 0 error / 0 warning；`McpDiscoveryTests.RunAll()` 通过（含两条新断言）。
- 真机（Unity 2021.3.45f2c1，`localhost:8000`）：`discover` / `groups`（55 命名空间 / 57 tag）/ `list --kind prompts`（4 项）/ `find` / `schema` / `schemas` / `details --tag`（17 项）/ `prompt` / `resource` / `request groups`（裸路由与 `/groups` 均通）/ `call` + `status` 全部通过；`call` 后落盘 `completed`，与 JS 输出逐字段一致；库 API `workflow_context` 正确记录 1 成功 1 失败。

## 本轮：门面改名 get_tool，热重载不再漂移

> 用户：「为何门面工具里面是 get_resource、get_prompt，而工具是 search_tool，名字咋还有区别，现在的流程是啥，为啥还要有 search_tool 这个东西，另外 unity 一热重载，应该不要触发 ai 重新获取工具列表和详情。」

### 一、为什么原来不一致

- `get_prompt` / `get_resource` 是 MCP 原语 `prompts/get`、`resources/read` 的 tools/call 兜底版，名字刻意沿用原语动词；`search_tool` 是 `tools/list` + `tools/get` 的兜底，却按"动作"取名，且它并不做搜索（只有 `type=list` 全量枚举与 `type=detail` 按名取详情）。`async_call` 移除后同组只剩三个，不统一暴露出来。

### 二、两条通路与 search_tool 的真实位置

| 通路 | 流程 | 结论 |
| --- | --- | --- |
| MCP 客户端 | `initialize` → `tools/list` → `tools/call` | `tools/list` 全量发布 518 项 / 302 KB，客户端本地已有全部定义，门面冗余 |
| 技能脚本 / GUI | `/discovery` → `/groups` → `/tools` → `/tools/{name}/schema` → `tools/call` | 功能被 REST 端点完全覆盖，门面同样冗余 |

- 唯一仍站得住的场景：`EnableDescriptions=false`（省 token 模式）时 `tools/list` 剥掉描述，客户端只能靠 `type=detail` 把参数说明要回来。
- 决策：保留门面但改名 `get_tool`，与另外两个同构；`tools/list` 维持全量（302 KB 由客户端缓存，只在重连时重传），不瘦身。

### 三、改动

- `Editor/Executer/SearchTool.cs` → `GetTool.cs`（保留原 `.meta` guid），类名 `SearchTool` → `GetTool`，`ToolName` → `get_tool`；描述改为"按名称获取工具定义"。
- `McpService`：`AddSearchMcpTool()` → `AddGetMcpTool()`，注册名与 `GetEnabledToolNames()` 同步；`CapabilityTool` 示例名、`McpServiceGUI` 分组注释、`docs/index.html`、用户使用指南、`McpDiscoveryTests` 断言（含反射调用的方法名）一并更新。
- **未加兼容别名**：工具名以 `tools/list` 为准，旧名直接消失。
- `McpConnectionInfo`：`InstanceId` 由 static 初始化器的 Guid 改为存 `Editor.SessionState`（键 `UniMcp.InstanceId`），`[InitializeOnLoadMethod] RestoreInstanceId()` 在每次域加载后取回；删除从未被调用的 `RenewInstanceId()`。
- 新增 `McpConnectionInfo.DomainId`（每次域加载新生成）：`ToolTaskStore.EnsureOrphansMarked` 与 `Query` 的 `from_previous_session` 改按域判定，条目新增 `domainId` 字段（旧记录回退比对 `instanceId`）。这样**热重载保住客户端绑定，同时把被重载杀死的 running 任务标成 `interrupted`**。
- 修重放缺陷：跨重载回放时账本结论原被写成 `{"replayed":true}` 占位符，同会话第三次查该 id 只剩占位符；现在账本写入落盘库里的真实结论，`failed` 状态一并透传。

### 四、实机验证（Unity 2021.3.45f2c1）

- 连续 5 次 `engine.recompile`，`/discovery` 的 `instance_id` 恒为 `7350a064a641451c8fd543bbc79123e8`。
- 上一域遗留 `running` 记录 → 重载后 `interrupted` 且 `from_previous_session:true`；已完成记录跨重载仍 `completed`。
- 同 id 重发（重载后）0.13s 回放；再次同 id 走账本，返回完整结论而非占位符。
- `tools/list` = 518，`/tools` 含 `get_tool` 且不再有 `search_tool`；`get_tool type=list` 返回 515 个声明式工具。
- 编译 0 error / 0 warning；`McpDiscoveryTests.RunAll()` 通过；JS `node --test` 66/66。
- `SKILL.md` 同步：域重载与脚本重编译不换实例标识，不必重新 `discover`、不必重取工具与 Schema；只有 Unity 进程重启才漂移。

## 历史：移除 async_call，调用通道自带落盘与提前返回

> 用户：「unity 工具调用执行，还加了个 async_call，不过他貌似没啥用了，能不能移除掉。然后确保正常工具的调用返回能进任务保存（和异步调用一样，当前任务落盘，并支持及时重启 unity 也能查状态），如果是异步任务耗时比较久，unity 内的工具也可以自动提前返回任务信息，带上任务 id。」

### 一、移除 async_call

- 删除 `Editor/Executer/AsyncCall.cs(.meta)`、`Editor/Model/AsyncCallResultStore.cs(.meta)`；内置入口收敛为 **3 个**（`get_prompt` / `get_resource` / `get_tool`），实时目录 518 项。
- `McpService`：去掉 `AddAsyncMcpTool()` 与 `GetEnabledToolNames()` 里的注册、`AdaptToolArguments()` 的专用分支、执行记录里的 `async_call.<func>` 特判；`CapabilityTool` 的拒绝阈值同步为 `ToolCatalog.Count + 3`。
- `McpDiscoveryTests` 内置断言改为 3；原「`groups[0].counts`」断言早已过时（`/groups` 现输出 `namespaces` / `tags`），改为校验保留命名空间 `_` 恰好 3 项。

### 二、正常调用结论落盘

- 新增 `Editor/Model/ToolTaskStore.cs`（`ScriptableSingleton`，`ProjectSettings/UnitySkilledMcpTasks.asset`）：字段 `id / tool / args / status / resultJson / error / instanceId / startedAt / completedAt`；`running | completed | failed | interrupted`；上限 100 条、单条结论 512 KiB、参数 8 KiB、结论总预算 4 MiB，超预算先清最旧结论并留状态。
- `McpService` 在 `tools/call` 的入队处 `MarkRunning`、工具回调收尾处 `Complete`（同时写内存账本），绑定失败/内部异常时 `Interrupt`；因此**提前返回的调用同样会落盘**。
- `/requests/{id}` 改为 `QueryTaskState`：落盘库优先（状态、工具名、起止时间、结论齐全），库内没有才回落内存账本；跨会话返回 `recorded_by_instance` 与 `from_previous_session`。
- `Start()` 预热单例并调用 `EnsureOrphansMarked()`：只把**非本会话**的 `running` 标记为 `interrupted`（`instanceId` 比对），避免把当前正在执行的任务误判；该清理原先只在 `async_call` 入口执行，服务重启后 `/requests/` 查不到，故前移到启动流程。
- 读取路径加锁（`entries` 的读与写都在 `gate` 内），长任务占住主线程时后台仍能查到 `running`。
- 跨会话重放保护：`ledger.Accept` 返回 `accepted` 后先查落盘库，若同一 `request_id` 已有终态则直接回放（`replayed:true`）不再执行；`interrupted` 以 `-32004` 要求换新 id。避免 Unity 重启后客户端重试造成副作用二次执行。

### 三、长耗时工具提前返回

- `tools/call` 的 10s 错误超时改为**成功语义的提前返回**：`{success:true, status:"running", task_id, tool, waited_seconds, persisted:true, query_path, message}`，包在标准 `content[].text` 里；工具继续在主线程执行，完成时自动落盘。
- 阈值可在 `UserSettings/McpLocalSettings.asset` 的 `_toolEarlyReturnSeconds` 调整（默认 10，范围 1–600，非法值回退默认）。
- `system.tasks` 的 `task_id` 现在同时支持批任务与工具调用任务：先查批任务，再查落盘库；无参数时列表里新增 `tool_calls`（原 `legacy_async` 字段移除）。

### 四、实机验证（Unity 2021.3.45f2c1）

- 编译 **0 error / 0 warning**；`McpDiscoveryTests.RunAll()` PASS（顺带补齐安装提示词里缺失的 INI 路径与 `SKILL.md` 指向，两处既有断言失败）；JS `node --test` **66/66**。
- `/tools` 518 项且无 `async_call`；常规调用后 `UnitySkilledMcpTasks.asset` 立即出现 `completed` 记录。
- 12s / 60s / 90s 长调用：10s 返回 `status=running` + `task_id`，完成后 `/requests/{id}` 得到完整结论；运行中查询也能拿到 `running`。
- 强杀编辑器后重启：同一 id 仍返回 `completed` 与 `from_previous_session=true`；伪造旧会话 `running` 记录在重启后被标记 `interrupted`。
- 重启后用同一 `request_id` 重发：0.13s 直接 `replayed:true` 回放，未二次执行（原本要 12s）。
- 任务库加入 `demo/.gitignore`（本机运行时产物，不入库）；旧的 `UnitySkilledMcpAsyncResults.asset` 已删除。

## 本轮：12 个扩展模块（尚未提交）

### 一、范围与分组

12 个扩展 `[ToolModule]`、82 个 `[ToolAction]` 使用下表简短前缀；`scene`、`ui`、`editor`、`project`、`audio` 合并到已有前缀，`optional_runtime` 保留。完整动作声明核查无冲突，无需改动作名，不添加旧前缀别名；既有工具和实现类名不变。`ToolCatalog` 按完整名注册，`ToolCall` / `ToolSchema` 按实际 Owner 的 `IStrictToolModule` 标记校验。核心分组仍为 10 个，包内仍为 55 模块 / 446 动作。

| 模块 | 分组 | 动作数 | 主要动作 |
| --- | --- | --- | --- |
| `scene` | Scene | 9 | `transform_get` / `transform_set` / `batch_active` / `scenes` / `open_additive` / `set_active_scene` / `move_to_scene` / `close_scene` / `missing_references` |
| `asset` | Assets | 7 | `dependencies` / `reverse_dependencies` / `texture_rule` / `validate` / `bundle_assign` / `bundle_build` / `addressables_entry` |
| `animation` | Animation | 8 | `sample_clip` / `director_inspect` / `director_asset` / `director_bind` / `director_control` / `timeline_tracks` / `avatar` / `constraint_set` |
| `ui` | UI | 4 | `control_create` / `bind_event` / `audit` / `font_apply` |
| `editor` | Editor | 7 | `selection_get` / `selection_set` / `inspector` / `layout_snapshot` / `window_show` / `wireframe` / `preview` |
| `runtime` | Runtime | 6 | `profiler` / `memory` / `snapshot` / `snapshot_compare` / `snapshot_remove` / `input_ui` |
| `project` | Project | 6 | `build_scenes` / `player_settings` / `quality` / `defines` / `asmdef_validate` / `health` |
| `system` | System | 7 | `tasks` / `cancel` / `batch_active` / `audit` / `data_validate` / `save_write` / `save_read` |
| `physics` | Physics | 9 | `rigidbody_set` / `collider_add` / `raycast` / `overlap` / `collision_matrix` / `joint_add` / `navmesh_path` / `navmesh_sample` / `navmesh_destination` |
| `rendering` | Rendering | 10 | `light_set` / `lightmaps` / `lightmap_assign` / `bake_start` / `bake_cancel` / `probe_inspect` / `probe_render` / `pipeline_get` / `pipeline_set` / `volume_set` |
| `audio` | Assets | 5 | `source` / `source_control` / `listeners` / `mixer_parameter` / `snapshot_transition` |
| `optional_runtime` | Runtime | 4 | `netcode_status` / `network_object` / `xr_loaders` / `xr_devices` |

### 二、安全与兼容

- `unity-package/Editor/Utils/ExtensionSafety.cs` 统一处理：必填/未知参数、类型与枚举、数字范围、`Assets/` 路径穿越与符号链接、场景对象有效性、`Undo` + `SetDirty`。
- 严格校验仅在实现 `IStrictToolModule` 的新模块上由 `ToolCall` 执行，`ToolSchema` 同步输出 `additionalProperties:false`；旧模块 schema 与行为不变。
- `SimpleJson` 增加“token 是否带引号”的内部标记，使新校验能区分 `1` 与 `"1"`；默认序列化与既有解析行为不变。
- 可选包（Addressables、Timeline、Netcode、XR Management、SRP Core）一律通过 `OptionalType` 反射访问，缺包返回明确错误，**不自动安装、不修改 `manifest.json`**。
- 破坏性与不可逆操作需显式参数：`bake_start` / `pipeline_set` 需 `confirm=true`；`close_scene` 默认拒绝丢弃脏场景；`save_write` 覆盖需 `overwrite=true`；批处理仅支持 `SetActive`，不代理任意工具。


## 本轮：内置工具分组收敛（19 → 8）（尚未提交）

> 用户：「内置工具集合的分组，重新分配一下，现在组太多了」

### 一、问题

46 个 `[ToolModule]` 声明了 **19 个不同分组**，碎片化严重：`Resource Management / 资源管理` 一家独吞 **17 个模块**（mesh / model / material / shader / texture / sprite_atlas / terrain / prefab / audio / script / scriptable_object / particle / anim_clip / scene / gameobject / component / object），而 `camera` / `effects` / `animation` / `texture generation` / `testing` / `ui` 等又各自单列。分组是 `groups` / `details --tag` 的标签来源（`Slug(GroupEn)`），组太多既稀释了标签语义，也让 AI 现场发现时难以按域收敛。

### 二、新分组方案（8 组）

按「操作对象所处的工作阶段」重排，兼顾内置与扩展模块：

| 分组 | 标签 | 归入模块（前缀） |
| --- | --- | --- |
| 场景层级 | `scene` | hierarchy、gameobject、scene、component、object |
| 资源资产 | `assets` | terrain、prefab、mesh、model、material、audio、script、sprite_atlas、scriptable_object、shader、texture、**texgen、probuilder**（扩展） |
| 动画与特效 | `animation` | animator、effects、anim_clip、particle |
| 界面 | `ui` | ui、ugui、**figma、ui_rule、uisourcegen**（扩展） |
| 编辑器 | `editor` | console、camera、scene_view、game_view、editor |
| 运行与调试 | `runtime` | gameplay、engine、tests |
| 项目工程 | `project` | project、package、tag_layer |
| 系统工具 | `system` | http、capability、prefers、source_location、python、code、**cmd、asset_index、webpage**（扩展） |

### 三、改动清单

- **包内 39 个模块**的 `GroupEn` / `GroupZh` 全部改写：`ResEdit/` 下 13 个资源类拆入 场景层级 / 资源资产 / 动画与特效；`Hierarchy/`（3）、`Projet/`（3）、`Window/`（3）、`Setting/`（3）、`Storage/`（2）、`RunCode/`（2）、`UI/`（2）、`Console/`（1）及根级 `BaseEditorTool` / `EngineTool` / `GamePlayTool` / `ObjectTool` / `HttpTool` / `TestsTool` 逐一套用新组。
- **demo 扩展 8 个模块**同套分组：`texgen` / `probuilder` → 资源资产，`figma` / `ui_rule` / `uisourcegen` → 界面，`cmd` / `asset_index` / `webpage` → 系统工具。
- **测试夹具同步**：`McpDiscoveryTests.cs` 的 `[CapabilityTags("Scene", ...)]`、`ToolNameAttribute(..., "Scene", "场景层级")` 与断言标签 `hierarchy-management` → `scene`；否则稳定标签断言会失败。
- **注释同步**：`ToolNameAttribute.cs` 参数文档示例由旧组名改为 `Scene` / `Assets`。
- **文档同步**：`README.zh-CN.md` 工具分类表重写为 8 分组表格 + 扩展模块归组说明；示例 `[ToolModule("animator", ...)]` 的 `GroupZh` 由「动画」→「动画与特效」（`README.md` 同步）；`docs/index.html` 工具生态卡片重排为 8 组 + 新增分组标签徽标，`docs/styles.css` 补 `.tool-tag` 样式与 `h4` flex 对齐；`unity-package/Unity3d_MCP_用户使用指南.md` 分类表由旧 10 类改为 8 分组。

> **不改的**：工具名（始终 `前缀.动作`）、各模块 `Prefix`、`ToolAction` 本身。分组仅影响 `groups` 返回的标签与 `details --tag` 的筛选，属纯元数据调整。技能 `SKILL.md` 走**动态发现**（标签一律取自 `groups` 返回），无硬编码分组，无需改动。

### 四、验证

- 实测分组分布：8 组（另 `Uncategorized` 为属性默认值，非实际分组）。
- 编译：`dotnet build demo/UniMcp.Editors.csproj -t:Rebuild` → **0 warning / 0 error**，DLL 重建时间与当前一致。
- 回归：`node --test tests/test_unity_bridge.js` → **59/59 pass**。
- HTML：`div` 290/290、`section` 10/10、`ul` 18/18 全部平衡。

---

## 本轮：内置能力管理（tools / prompts / resources）+ 修复配置项未发布（尚未提交）

> 用户：「还缺少配置 unity 端资源和提示词的技能，mcp 关键有工具资源提示词，这个配置功能需要内置到框架中，让 ai 能发现并能修改查询」；「还需要开关工具的能力」

### 一、背景：三处真实缺陷

排查中发现 MCP 三原语的管理能力存在结构性缺口：

1. **配置的提示词/资源从未被发布**（最严重）。`DiscoverPrompts()` / `DiscoverResources()` 靠反射扫描 `IPrompts` / `IRes` 实现类型，却**显式排除** `ConfigurablePrompt` / `ConfigurableResource`；而 `McpSettings.GetConfigurablePrompts()` / `GetConfigurableResources()`（GUI 里「添加提示词/资源」写入的对象）**没有任何代码把它们并入** `availablePrompts` / `availableResources`。结果：用户在 GUI 里配置了提示词或资源，`prompts/list` / `resources/list` 里永远看不到。
2. **开关能力齐备但 AI 够不着**。`McpLocalSettings` 已有完整的 `SetToolEnabled` / `SetToolsEnabled` / `SetPromptEnabled` / `SetPromptsEnabled` / `SetResourceEnabled` / `SetResourcesEnabled` 与对应 `Is*Enabled` / `Filter*`，GUI 也在用；但没有任何 `[ToolModule]` 把它暴露给 MCP 客户端。
3. **缺少注册表自省入口**。AI 无法查询"当前有哪些工具/提示词/资源、哪些被禁用"，只能靠 `tools/list` 这类协议接口间接感知，提示词与资源的启用状态更是完全不可见。

### 二、修复：配置项并入发布流程

- `McpService` 新增 `RegisterConfigurablePrompts()` / `RegisterConfigurableResources()`，在 `DiscoverPrompts()` / `DiscoverResources()` 末尾调用，把 `McpSettings` 中的配置项显式登记进 `availablePrompts` / `availableResources`。
- 提示词缺正文、资源无法解析 URL 时跳过并告警；同名时配置项覆盖代码实现（尊重用户意图）。
- 资源侧先调 `EnsureUrlCalculated()`（`UnityObject` 类型需在主线程算 `file://` URL）。

### 三、新增：`capability` 内置模块（6 动作）

`unity-package/Editor/Tools/Setting/CapabilityTool.cs`，`[ToolModule("capability", GroupEn="System Management", GroupZh="系统管理")]`：

| 动作 | 作用 |
| --- | --- |
| `list` | 按 `kind`（tool/prompt/resource）列出全部能力及启用状态，支持 `filter` 子串过滤 |
| `get` | 读取单项详情：工具返回参数表；提示词返回正文与参数；资源返回 URI 与 MIME |
| `set` | 启停能力。工具支持**分组前缀**（如 `name="texgen"` 一次开关整组）；全量禁用需 `confirm="DISABLE_ALL"` |
| `save` | 新增/更新可配置提示词或资源（写入 `McpSettings.asset`），支持 `argument_names/types/required` 声明参数 |
| `remove` | 删除可配置的提示词或资源（代码定义的只能禁用，不可删） |
| `settings` | 读写通用设置：`enable_descriptions` / `language` / `max_details`，并返回端口、工具计数快照 |

### 四、安全与一致性设计

- **统一重发现入口** `McpService.RefreshDiscovery()`：一次清 `tools/prompts/resources` 列表缓存并重发现，所有变更动作调用它，使改动立即对已连接客户端可见（`list_changed` 语义）。
- **自锁保护**：`capability.set` 是经 MCP 恢复任何开关的唯一通道，因此禁止禁用它自身；`DISABLE_ALL` 分支也显式豁免它，确保恢复通道永远敞开。
- **`save` 自动启用**：显式保存即视为想使用，自动清掉可能残留的同名禁用记录。
- **新增公开只读访问器**：`McpService.GetAllPrompts()` / `GetAllResources()`（含被禁用项），供自省工具查询。
- 全部状态仅落在 `ProjectSettings/McpSettings.asset`（配置项）与 `UserSettings/McpLocalSettings.asset`（开关），不越界写盘。

### 五、GUI：资源 MIME 类型改为纯下拉选择

> 用户：「gui 上 resource 列表中的元素，配置 mimetype 需要调整为下拉选择，常用的资源格式即可」

- `McpServiceGUI` 资源详情原为「文本框 + 下拉框」双控件，改为**单一 `EditorGUILayout.Popup`**，只列出常用资源格式（`commonMimeTypes`，29 项：文本/JSON/XML 类、图片、音视频、压缩包）。
- 若资源的当前 MIME 不在常用列表内，**临时置顶保留自定义值**，避免打开面板即被静默改写；选择后写回 `resource.SetMimeType()` 并保存。
- 删除已无用的 `resourceMimeTypeSelections` 索引字典。

### 六、GUI 刷新统一走 `RefreshDiscovery()`

- 资源/提示词的启停、增删、新增等处原只调 `RediscoverTools()`，**不清 `prompts/list` / `resources/list` 缓存**，导致开关后客户端仍读到旧列表。
- 统一改为 `McpService.RefreshDiscovery()`（重发现 tools + prompts + resources 并清三类列表缓存），与 `capability` 工具行为一致。

### 七、验证与文档

- **编译**：临时镜像工程（引用真实 Unity 托管 DLL）**0 error / 0 CS 警告**；首轮曾报 `CS1061 SetDefault`（`MethodBool` 链式调用括号错位）已修正；另修 `McpDiscoveryTests.cs` 缺 `using System.Linq`（`CS1061 Any`）。首轮 0 error 曾因 `CapabilityTool.cs` 未计入编译列表而失真，补入后复验通过。
- **计数**：包 = **43 模块 / 364 动作**（+1 模块 / +6 动作），demo = 7 / 47，注册合计 **50 模块 / 411 工具**。
- **文档**：`README.zh-CN.md`（工具层与工具域表 + `capability (6)` 行与说明）、`docs/index.html`（hero 405/49 → **411/50**）、本节重写。HTML 标签平衡校验通过。

---

## 本轮：TexGen 贴图引擎（demo 自定义工具）+ `skill.ini` 发布技能源目录（已提交 2ddcb17）

> 用户：「不调用任何外部 AI、不发 HTTP、不依赖 SD / 云端 API；纯 C# Editor 代码，内置一套程序化绘图原语……输出 PNG。本质：做一个矢量绘图原语引擎（在 Texture2D 像素画布上）……实现一个新的纹理生成技能」；「TexGen 放到 demo\Assets\Extends\CustomTools 中」；「skill.ini 还要带上 unity-skilled-mcp 技能源目录」

### 一、TexGen 程序化贴图引擎

**设计定位**

- **不是 AI 画图**：上层 LLM 把想法翻译成**指令列表（不是 prompt）**，本引擎用 C# 执行像素光栅化、导出 PNG —— 「AI 编排绘图指令，C# 负责渲染像素」。
- **零外部依赖**：无 HTTP、无 SD、无云端 API、无第三方库；仅依赖 `UnityEngine`（`Texture2D` / `Color` / `Mathf`）。
- **能力边界**：技能图标、遮罩、PBR 灰度图、法线、光效、渐变、噪波、形状、线条、圆环、波纹、渐变叠加、混合层、遮罩裁剪；**做不了照片级复杂纹理**。
- **归属**：按用户要求放在 **`demo/Assets/Extends/CustomTools/TexGen/`**（demo 自定义扩展工具，不随包分发），与 ProBuilder 等示例同级。

**新增文件（`demo/Assets/Extends/CustomTools/TexGen/`，6 源 + 7 meta）**

| 文件 | 职责 |
| --- | --- |
| `TexGenNoise.cs` | 确定性整数哈希噪波：`Value` / `Perlin` / `Fbm` / `Ridged` / `Worley`；纯函数，同输入同输出 |
| `TexGenModel.cs` | 指令模型（`TgSpec` / `TgLayer` / `TgGradient`）、枚举（`TgShape` / `TgFill` / `TgBlend` / `TgNoiseKind` / `TgOrigin`）、`TgJson` 读取器（hex / r,g,b / 命名色 / JSON 数组） |
| `TexGenParser.cs` | `TgParser.Parse(JsonNode)` → `TgSpec`；校验（多边形≥3 点、ring 内径<外径、零长线告警、维度 clamp）；`blend` 关键字映射（`mask/clip/stencil`→保留内部、`erase/cutout/hole`→挖洞） |
| `TexGenRasterizer.cs` | 核心：SDF 覆盖率光栅化 + SSAA 超采样（1–4×）+ W3C 预乘混合；填充（纯色/线性/径向/噪波/波纹/网格）；混合（正常/叠加/相乘/滤色/遮罩/擦除）；图层栈；可选法线贴图；PNG 导出 |
| `TexGenTool.cs` | `[ToolModule("texgen", GroupEn="Texture Generation", GroupZh="程序化贴图")]`，11 个 action：`render` / `batch` / `validate` / `spec` / `examples` / `preview` / `normal_from_image` / `uv_info` / `for_mesh` / `pbr` / `templates` |
| `TexGenPreviewWindow.cs` | `Window/MCP/TexGen Preview` 菜单；4 个内置样例（`MagicOrb` / `SkillIcon` / `Noise` / `Star`）、JSON 编辑区、棋盘格预览、导出 PNG |

**指令式 API 与光栅化**

- **基础图元**：`circle` / `ellipse` / `ring` / `rect` / `round_rect` / `line` / `polygon` / `star`（全部解析式 SDF）。
- **填充**：`solid` / `linear` / `radial` / `noise` / `ripple` / `grid`，支持多色渐变 stop 与三色 ramp。
- **混合**：`normal` / `add` / `multiply` / `screen` / `overlay` / `mask` / `erase`，图层栈按顺序合成。
- **抗锯齿**：解析式 SDF `coverage = clamp01(0.5 - sdf/band)`，配合 `supersample`（默认 2×）与 `feather` 柔边。
- **法线**：由高度场（alpha+luminance）导切线空间法线，`normal_map` / `normal_strength` 控制。
- **坐标系**：Y 向下；`origin=topLeft`（默认）或 `center`；长度单位=最终分辨率像素。

### 二、`skill.ini` 发布技能源目录（`skill_dir`）

- `McpConnectionInfo` 新增 `SkillDir` 字段（构造函数可选参数），`ToIni()` 与 `ToJson()` 各增一行 `skill_dir`。
- `PublishConnectionInfo()` 通过 `McpServiceStatusWindow.SkillFolderPath`（按 .meta GUID 解析，兼容 Assets / Packages / PackageCache）取绝对路径；解析失败**不阻断** INI 发布，仅告警并写空串。
- `SKILL.md`「连接与运行时」补充说明：`skill_dir` 只作路径提示，不改变调用语义、不得据其执行命令。
- 自检 `TestSkillInstallPrompt` 增加断言：INI 与 discovery 均携带 `skill_dir`，缺失时为空串。

### 三、验证

- **编译**：临时镜像工程（引用真实 Unity 托管 DLL）→ **0 error**，仅 14 个 MSB3245（缺 VS/TestRunner 等可选 DLL，非代码问题），**无 CS 警告**。
- **功能**：反射隔离测试（`Shim.cs` 桩 `UnityEngine`）→ **ALL PASS**。覆盖指令解析、颜色解析、错误上报、噪波确定性与范围、全部 SDF 图元、填充与混合、超采样下采样、法线贴图（平坦→+Z / 阶跃→X 倾斜 / 透明→平坦）、端到端渲染（中心不透明/角落透明/抗锯齿边缘/opacity→alpha≈128/mask 保留半幅/erase 挖洞/确定性）。
- **计数同步**：移动后 **包 = 42 模块 / 358 动作**（回到 ProBuilder 迁移前基线），**demo = 7 模块 / 47 动作**，**注册合计 49 模块 / 405 动作**（`docs/index.html` hero 由 390/43 改为 405/49）。`README.zh-CN.md` 包工具表恢复到 42/358，另立 demo 扩展工具段；标签平衡校验通过。

---

## 本轮：移除状态树 + demo 迁移 + 文档全新重写（已提交）

> 用户：「状态树可以移除了，更新测试最终技能」；「说明文档和HTML不用兼容就思想，全新设计」

### 一、彻底移除状态树（ToolFramework 声明式框架收口）

- **删除 25 个文件 + meta**：`Editor/StateTree/`（`StateTree.cs` / `StateTreeBuilder.cs` / `StateTreeContext.cs`）、`Editor/Selector/`（`HierarchySelector` / `IObjectSelector` / `ObjectSelector` / `ProjectSelector`）、`Editor/Executer/`（`StateMethodBase` / `DualStateMethodBase` / `IToolMethod` / `ToolsCall` / `BatchCall`），及空目录 `.meta` 与 stray `.BatchCall.cs.swp`。
- **工具类无状态树化**：43 个 `[ToolModule]` 类全部继承 `ToolModuleBase`；handler 直接标 `[ToolAction]`；`StateTreeContext` → `ToolContext`；`IToolMethod { ExecuteMethod }` → `ToolModuleBase`。
- **目标定位契约**：新增 `ITargetedTool.ResolveTarget(ToolContext)` + `ToolTargetResolver.Resolve<T>(ctx, TargetScope { Any, Hierarchy, Project })`；`ToolCall` 在 handler 前解析目标并存入 `_resolved_targets`。5 个目标定位工具（`ObjectTool` / `ComponentTool` / `ParticleTool` / `GameObjectTool` / `UGUILayoutTool`）各自实现。
- **服务层收口**：`McpService` 删 `availableTools` / `methodsCall` 字段与死代码；`AsyncCall` / `McpDebugWindow` / `McpServiceGUI` / `McpSettingsProvider` / `McpDiscoveryTests` 全部改走 `ToolCatalog` + `toolInfos`。
- **验证**：`dotnet build demo/UniMcp.Editors.csproj` / `UniMcp.Runtime.csproj` = 0 error / 0 warning；全仓无 `IToolMethod` / `ToolsCall` / `BatchCall` / `StateTree*` / `IObjectSelector` 悬空引用；清理 41 个 `.cs.bak` 恢复备份。

### 二、迁移 demo 自定义扩展工具（实机回归）

- 移除状态树后，`demo/Assets` 中 5 个 Editor 侧自定义工具仍引用被删符号，实机重编译报 16 个错误。逐文件迁移：
  - `CMCmdTool.cs` → `[ToolModule("cmd", …, LegacyName="cmd_tool")]`，默认 Leaf 改为具名 `[ToolAction("run")]`。
  - `AssetIndexManage.cs` → `[ToolModule("asset_index", …)]`，9 个 action；删 `HandleUnknownAction`。
  - `WebPageManager.cs` → `[ToolModule("webpage", …)]`，7 个 action；删 `HandleUnknownAction`。
  - `FigmaManage.cs` → `[ToolModule("figma", UI Management/UI管理, LegacyName="figma_manage")]`，4 个 action。
  - `UIRuleManage.cs` → `[ToolModule("ui_rule", …)]`，5 个 action。
- `demo/Assets/Runtime/RuntimeToolsExample.cs` 属**独立 runtime 包**（`UniMcp.Runtime`，未移除状态树），保持原样不动。
- **验证**：
  - `dotnet build demo/Assembly-CSharp.csproj` / `Assembly-CSharp-Editor.csproj` = 0 error / 0 warning。
  - 实机 `engine.recompile` → `engine.get_compilation`：`error_count=0, warning_count=0`。
  - `/tools`：367 → **393**（ToolCatalog 390 + 3 服务级工具）；26 个 demo 工具全部注册；`system-tools=10` / `network-tools=15` / `ui-management=12`。
  - 实机冒烟：`asset_index.categories` / `webpage.categories` / `webpage.add`→search→remove 全通；`cmd.run` 可执行；Unity 菜单「Run Discovery Self Tests」：**服务发现自检通过**。
  - 技能 JS `node --test`：**59/59**。
- 清理上轮漏删的 41 个孤儿 `*.cs.bak.meta`。

### 三、文档全新重写（不保留旧架构兼容表述）

- `README.md`（英文）：已按新架构；本轮核对无状态树残留。
- `README.zh-CN.md`：**整篇重写**（1359 → 约 560 行）。旧版大段描述 `StateTree` / `Selector` / `StateMethodBase` / `DualStateMethodBase` / `IToolMethod` 及其源码结构树、双层调用架构「FacadeTools + MethodTools」全部删除；改为「动作即工具 + Attribute 反射 + 无状态树」的真实架构，新增「工具框架」与「扩展新工具」两节、Runtime 包与 Editor 包的边界说明、43 模块 / 364 动作的真实工具表。
- `docs/index.html`：本轮核对，架构叙事已是新框架；清理残留旧工具名（`hierarchy_create` / `edit_gameobject` / `code_runner` / `console_read` → 二级名）。
- 本 `note.md`：新增本节。

### 四、待办
- 重建 `dist/unity3d-skill.zip`。
- 所有改动**未提交 / 推送**（仓库仍停在 `af145de`）。

---

## 历史记录（架构已废弃，仅存档）

> 以下章节描述的 `DefineTools` / `*.Tools.cs` / 状态树 / 假合并 / 提供类等架构**均已删除**；工具名、命令示例亦多已失效。仅作改动档案保留。

# 最近修改说明（2026-11-05）

> 第一～六节对应提交 `744730b 更新 技能说明`（30 个文件，约 +7900 行）；第〇节是之后的运行时代码执行方案与其模式化合并；其后各节是技能契约收敛、仅 JS 运行时、caveman 精炼与实机验证，已随 `26716d6`、`af145de` 提交并推送三个远程。**最前面四节是之后尚未提交的改动**：把「假合并」还原为真实类、C# 工具加载重构、移除 sync_call / async_call 结论落盘 / 同域提供类合并、以及 Attribute + 反射的单文件模块。文中早期章节描述的 `DefineTools` / `*.Tools.cs` 架构已被最新一节取代。本文件只记录改动，不是完整项目文档；完整用法见 `SKILL.md` 与 `README.md`。

## 本轮：消除「假合并」，能合则真合、合不了拆真实类（尚未提交）

> 用户：「那还是拆多个工具类得了，能合一个类就合一个类，合不了保持多个类，最好要合一个类，不是那种一个类里面嵌套多个类的假合并」

- 上一轮把 `console` / `hierarchy` / `project` 三个多类领域物理并入单文件、并以 `internal sealed class XxxImpl` 嵌套隔离——即用户所指的「假合并」。本轮按「能否真合并」重新处置。
- **判定依据**：`StateMethodBase` / `DualStateMethodBase` 的状态树以**首键**路由（Console 用 `action`；Hierarchy 的 create 用 `source`、search 用 `search_type`；Project 的 create 用 `source`、search 用 `search_target`）。**只有共享同一判别首键、且无 action 名冲突、无同名成员冲突的 impl 才能真合并**；否则必须保留多个独立顶层类，否则 Inject 注入的原始参数无法正确路由。
- **`ConsoleTool` 真合并为单个真实类**：`ConsoleRead` / `ConsoleWrite` 首键同为 `action`，唯一重名成员是那些「本应唯一」的 `CreateKeys` / `CreateStateTree` / `Description` / `HandleUnknownAction`，合并后自然消除。现为 `public sealed class ConsoleTool : StateMethodBase`，统一 9 个 key、统一以 `action` 分支的 `CreateStateTree()`，全部 handler 为该类直接成员。删除 `[LegacyToolName]`（不存在的特性）误写。
- **`Hierarchy` 还原为 3 个真实顶层类**：`HierarchyApplyTool.cs`（`LegacyName="hierarchy_apply"`，5 action）、`HierarchyCreateTool.cs`（`hierarchy_create`，12 action，首键 `source`）、`HierarchySearchTool.cs`（`hierarchy_search`，8 action，首键 `search_type`）；删除旧 `HierarchyTool.cs` + meta。
- **`Project` 还原为 3 个真实顶层类**：`ProjectCreateTool.cs`（`project_create`，5 action，首键 `source`）、`ProjectOperateTool.cs`（`project_operate`，16 action，首键 `action`）、`ProjectSearchTool.cs`（`project_search`，15 action，首键 `search_target`）；删除旧 `ProjectTool.cs` + meta。
- **跨类引用更新**：`GameObjectTool` 中 `private HierarchyTool.HierarchyCreateImpl hierarchyCreate;` → `private HierarchyCreateTool hierarchyCreate;`，实例化同步改为 `new HierarchyCreateTool()`；`hierarchyCreate.ExecuteMethod(args)` 不变（拆出的类仍是 `StateMethodBase`）。
- **声明式目录/GUI 联动**：`ToolCatalog` 不再依赖嵌套类型扫描（`ownerTypes.AddRange(type.GetNestedTypes(...))` 路径不再被命中）；`McpService` 新增 `GetAllToolSpecs()` / `GetBuiltinToolInfos()`；`McpServiceGUI` 工具树重做为 3 级（分组 → 模块 → 子函数 → 参数表）+ 内置门面；`McpDebugWindow` 单函数调用改为优先 `ToolCatalog.Find` → `new ToolCall(spec)`，旧名回退 `ToolsCall`。
- **编译验证**：`dotnet build demo/UniMcp.Editors.csproj` 连续三次 **0 error / 0 warning**；程序集内含 7 个新类名，`HierarchyApplyImpl` / `ProjectOperateImpl` / `ConsoleReadImpl` 等嵌套类计数为 **0**（已彻底消失）。总量守恒：`[ToolAction]` = 364、`[ToolModule]` = 43、`*Tool.cs` = 43（与旧「提供类 39」的差＝旧假合并把 3 模块各计 1 文件，现为真实文件）。动作名无重复（hierarchy 22 / project 33 / console 14）。meta 一致性：拆分目录下每个 `.cs` 均有 `.meta`，无孤立。
- **回归修复（本轮实测发现）**：声明式重构时 `McpService.GetMcpTool()` 删掉了旧的 `methodsCall.GetToolMethod(...)` / `SetToolName(...)` 二级方法回退，仅保留 `ToolCatalog.Find` → `new ToolCall(spec)` 与 `mcpToolInstanceCache`，导致所有 `[ToolName]` legacy 名（`hierarchy_create` / `code_runner` / `edit_animator` 等）在旧 MCP `tools/call` 路径下返回 `Tool not found`——而同一方法上方注释明确写着「保持原 MCP 调用链行为，含 ToolsCall 注册的二级方法」，`McpDebugWindow`（`:1479`）与 `AsyncCall`（`:114`）也都保留了该回退。已在 `GetMcpTool` 中恢复该回退段（置于 `mcpToolInstanceCache` 命中之后、返回 null 之前），`dotnet build` 仍 0/0。
- 注：本轮改动**尚未 git 提交或推送**；上述 legacy 回退修复需 Unity 重新编译后才会在当前运行实例生效（实测运行实例为修复前构建，`console_read` / `hierarchy_create` 等旧名在该实例仍不可用）。

## 本轮：Attribute + 反射，全部模块收敛为单文件 XxxTool（尚未提交）

> 用户：「我希望充分利用Attribute和反射来获得目前DefineTools收集函数参数的功能，这样就不需要一个单独的xxx.Tools脚本了。然后所有模块都改为xxxTool，比如ConsoleTool，BaseEditorTool,ObjectTool等等，只要一个脚本就解决了一类问题」

**框架层**

- 新增 `Editor/ToolFramework/ToolAttributes.cs`：`ToolModuleAttribute`（类级：prefix / group / legacy）、`ToolActionAttribute`（方法级：action / Parameters / Required / Inject，允许一个方法声明多个 action）、`ToolParamAttribute`（方法或强类型参数级）。
- `ToolCatalog.Reload()` 改为纯反射：扫描 `[ToolModule]` 类型及其嵌套实现类型 → 枚举 `[ToolAction]` 方法 → `Activator.CreateInstance` 建 owner → 从 owner 的 `Keys` 解析 `MethodKey` → `MethodInfo.Invoke` 包装成 `Func<JsonClass,object>` / `Func<StateTreeContext,object>` → 生成精确 `inputSchema`。描述文本也从 Attribute + 参数反射推导。
- 支持强类型 handler：参数上标 `[ToolParam("path")] string path` 时，框架按名从 JSON 取值并转换（string/int/float/bool/JsonNode 等）。
- 删除 `IToolProvider.cs` / `ToolBinder.cs`（含 meta）；`ToolSpec.Owner` 由接口改为 `object`，`ToolCall` / `ToolSchema` / `SearchTool` / `McpDiscovery` 无需改动。

**模块层**

- 39 个提供类统一改名 `XxxTool`：`BaseEditorTool` / `AnimatorTool` / `GameObjectTool` / `ObjectTool` / `EngineTool` / `CameraTool` / `CodeTool` …… 每个类自带 `[ToolModule]`，handler 上就地标 `[ToolAction]`，**一个脚本即一个领域的完整定义**。
- `console` / `hierarchy` / `project` 三个多类领域把 8 个原实现物理并入单个 `ConsoleTool.cs` / `HierarchyTool.cs` / `ProjectTool.cs`，以 `internal sealed class XxxImpl` 嵌套隔离，保留各自 `[ToolName]` 供旧注册表发现；零命名冲突、无需合并 `Keys`。**（已废弃，见最前面「消除假合并」一节：Console 真合并为单类，Hierarchy / Project 各还原为 3 个真实顶层类。）**
- 删除全部 **44 份 `*.Tools.cs` + meta**；普通类文件与 meta 一起改名（保留 guid）。`editor.execute_menu` / `editor.get_menu_items` 的外部静态方法改为本类转发方法，仍由 Attribute 声明。
- 迁移脚本 `mcp_attr_migrate.py` 从备份重置后生成，幂等可复跑；同时处理构造函数改名、`typeof(旧类名)` 自引用与跨类类型引用（`HierarchyTool.HierarchyCreateImpl`）。

**实机验证**

- 编译 0 error / 0 warning；`/tools` = **367**（364 小工具 + 3 内置）；`groups` = 56；`animator.create` 仅 `path`、`hierarchy.create_cube` 10 参数、`gameobject.modify` 含 `path/instance_id`、`editor.execute_menu` 经转发正常。
- 6 个工具调用、`McpDiscoveryTests.RunAll()` 自检、旧 MCP `tools/list`=367、`async_call` 落盘取回、JS `node --test` 59/59 全部通过。
- 旧注册表 legacy 名经 `GetMcpTool` 的 `methodsCall` 回退仍可解析：`hierarchy_create` → `HierarchyCreateTool`、`code_runner` → `CodeTool`、`edit_animator` → `AnimatorTool` 等。（`console_read` / `console_write` 两个旧名随原类删除而消失，统一收敛为 `console`。）

## 本轮：移除 sync_call / async_call 结论落盘 / 同域提供类合并（尚未提交）

> 用户：「移除sync_call，保留async_call，因为其他mcp接口直接调用即可，另外async_call需要确保调用后结论存本地asset，即使unity重启也能拿到结论。另外整和一下类似脚本如console_read，console_write合并为console工具提供类。」

**移除 sync_call**

- `McpService.GetEnabledToolNames()` / `RefreshToolInfos()` 不再注册；删除 `AddSyncMcpTool()` 与 `Executer/SyncCall.cs(.meta)`；`McpDiscoveryTests` 内置工具断言由 4 个改为 3 个（system 分组计数同步）。
- 内置入口现为 `async_call` / `get_prompt` / `search_tool`；实时目录 **367 = 364 小工具 + 3 内置**。

**async_call 结论持久化**

- 新增 `Editor/Model/AsyncCallResultStore.cs`：`ScriptableSingleton` + `[FilePath("ProjectSettings/UnitySkilledMcpAsyncResults.asset", Location.ProjectFolder)]`，结论以纯字符串写入 YAML asset；工程级、Library 重建亦不丢、不污染 `Assets/`。
- 重写 `Executer/AsyncCall.cs`：`type=in/out/list/remove/clear`（type 缺省 `out`，required 仅 `id`）；`func` 支持 `分组.动作`（`ToolCatalog.Find` + `ToolCall`）与旧 snake_case 名（`ToolsCall.InvokeMethodStatic` 兜底）。
- 完成即 `Save(true)` 落盘；`out` 只读不删，返回 `id/func/status/persisted/started_at/completed_at/result`；域重载后 `running` 孤儿标记 `interrupted`；最多 100 条、单条结论 2 MiB 上限。
- `AddAsyncMcpTool()` 的 schema 与描述同步（type enum、重启可读说明）。

**同域提供类合并**

- 新增 3 个聚合 `IToolProvider`：`ConsoleTool`（ConsoleRead + ConsoleWrite，14 个 `console.*`）、`HierarchyTool`（3 类 22 工具）、`ProjectTool`（3 类 33 工具）。**（聚合类形态已废弃，见「消除假合并」：Console 为真实单类，Hierarchy / Project 各为 3 个真实顶层类。）**
- 8 个原类去掉 `IToolProvider`，原 `*.Tools.cs` 改为仅暴露 `ToolParameters` 的 partial；相关 handler 改 `public` 供聚合类绑定；生成器 `mcp_generate.py` 内置 `MERGE_GROUPS` 且可重复运行。
- `console_write.*` 统一为 `console.*`；工具总数仍 364，**提供类从 44 收敛为 39**。
- 顺带修复生成器 CRCRLF 行尾（改用 `write_bytes`）。

**实机验证**

- Unity 2021.3.45f2c1 编译 0 error / 0 warning；`/tools`=367、`/groups` system=3、`sync_call` 不在目录、`console_write.*` 0 个。
- `async_call`：clear → in(`engine.get_info`) → out 得到完整结论；本地 asset 落盘并含 id/func/status；`type=list` 正常；未知 func 明确报错。
- **域重载实测**：改 C# 触发重编译后反射读 `orphansChecked=False`（静态内存确已清空），同一 `id` 的 `type=out` 仍返回 `status=completed`、`persisted=true` 与完整 `result`；`console.log` 调用成功。
- `McpDiscoveryTests.RunAll()` PASS；旧 MCP `tools/list`=367 且无 `sync_call`；`node --test` 59/59。

## 本轮：C# 工具加载重构 —— 声明式细粒度工具（尚未提交）

> 用户：「状态树的设计实在糟糕透顶，每一个 mcp 接口函数都应该参数明确，而不是一堆参数不明确的放一个大函数里面……拆成一个个小工具，并让一个类暴露多个工具接口……`edit_animator` 的 `action` 都要暴露为 `animator.xxx` 二级名称……批量调用的接口也可以不要了，因为技能已经支持代码的方式调用。」

**框架（新增 `Editor/ToolFramework/`）**

- `IToolProvider` / `ToolBinder` / `ToolSpec` / `ToolCatalog` / `ToolSchema` / `ToolCall`：每个类在 `DefineTools(binder)` 里声明多个工具，一工具一名、一工具一 `handler`、一工具一精确 `inputSchema`；`ToolCatalog` 是唯一真源，不再运行 `action` 状态树做路由。
- 44 个业务类统一改为 `partial class X : StateMethodBase, IToolProvider`（或 `DualStateMethodBase`），业务 handler 逻辑原样复用；每类生成一份 `*.Tools.cs` + `.meta`，共 **44 份声明 / 364 个唯一工具**，加上 4 个内置门面 = 实时目录 **368 项**。
- `McpService`：`DiscoverTools()` 改装载 `ToolCatalog`，新增 `RefreshToolInfos()`；`GetMcpTool()` 返回每次新建的 `ToolCall(spec)`，无共享分发状态；`AdaptToolArguments()` / `HandleToolsCall()` 去掉 `batch_call` 分支。`McpDiscovery.UpdateDiscoverySnapshot()` 改用 `ToolCatalog.All`；`SearchTool` 改用 `ToolCatalog.Find` + `ToolSchema.Build`。
- `DualStateMethodBase` 新增 `ExecuteWithHandler()`：仍用 `CreateTargetTree()` 解析目标，但绕过 `CreateActionTree()` 直接调绑定 handler。

**命名与参数**

- 二级名称如 `animator.create`、`animator.add_state`、`engine.get_info`、`hierarchy.create_cube`、`code.execute`；`action` / `source` / `confirm` 等由服务端注入，不再出现在 schema 里。
- 静态推断修正：调用链传播深度 3 → 8、同名方法体合并、本文件优先，补齐 37 个工具的参数（如 `hierarchy.create_cube` 现有 `position/rotation/scale/tag/layer/prefab_path`）；21 个 `DualStateMethodBase` 工具显式补上 `path` / `instance_id` 目标定位参数。
- 生成器幂等修复：第二轮运行曾把 44 个类声明写成 `partial partial ... , IToolProvider, IToolProvider`，Unity 报 88 个编译错误；已修复全部声明并让 `mcp_generate.py` 可重复运行。

**移除批量调用**

- 服务端不再发布 `batch_call`（`/tools/batch_call/schema` 返回 404）；JS 客户端删除 `calls` / `callMany` / `callsPayload`，`node --test` **59/59**。`McpDiscoveryTests.TestBuiltInSchemas()` 同步为 4 个内置工具。
- `SKILL.md` 删除「批量工具调用」整节，改为「多步访问」契约：由调用方用库 API 逐次调用、自行决定循环与失败处理；退出码去掉 `calls` 例外；description、API 表、方法列表同步。
- `README.md` / `README.zh-CN.md` / `docs/index.html` 去掉 `calls` / `batch_call` 示例，中文 README 顶部加「本轮重构」声明（旧 `hierarchy_create` / `edit_gameobject` / `code_runner` 等名称已失效），旧《用户使用指南》加废弃声明。
- `demo/skill_scene_detail.js` 迁移到二级工具名并改为逐次 `callSkillText` 编排，移除 `callMany`。

**实机验证（Unity 2021.3.45f2c1，localhost:8000）**

- 编译 **0 error / 0 warning**；`/tools` = 368，`groups` = 57，无 `batch_call`；`animator.create` 只有 `path`，`animator.add_state` 为 `path/layer/state/motion/speed`，`hierarchy.create_cube` 与 `gameobject.modify` 参数完整。
- `schemas` 一次取 3 个精确定义；`engine.get_info` / `console.get` / `animator.get_info` / `code.validate` 调用成功；旧 MCP `tools/list` 仍返回 368 项。
- `McpDiscoveryTests.RunAll()` 实机通过：测试夹具补齐 `responseCache` / `responseCacheLock` / `responseCacheExpiry` 与三类注册表（`GetUninitializedObject` 不执行字段初始化器，旧列表 handler 会读缓存锁而抛 `ArgumentNullException`）；同时把「旧列表应拒绝并引导 HTTP 发现」的过时断言改为「旧 MCP `tools/list` / `prompts/list` / `resources/list` 仍兼容返回」，与保留旧 MCP 流程的要求一致。
- `dist/unity3d-skill.zip` 重建：10 项、无 `.py`、包内 `SKILL.md` 与源文件字节一致。

---

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
- 验证：`node --test tests/test_unity_bridge.js` 64/64；`git diff --check`；代码块逐行比对无缺失；关键约束词全部在；`dist/unity3d-skill.zip` 重建后 10 项、无 `.py`、包内 SKILL.md 与源文件字节一致、`testzip()=None`。

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
| 技能包 | 删除 `scripts/unity_bridge.py`(+`.meta`)、`tests/test_unity_bridge.py`(+`.meta`)；`SKILL.md` / README / docs 只保留 `require('./scripts/unity_bridge.js')` 与 `node --test` |
| `McpLocalSettings.cs` | 删除 `_scriptLanguage` 字段、`ScriptLanguage` 属性、`NormalizeScriptLanguage()` 及 `OnEnable` 中的归一化调用 |
| `McpDiscovery.cs` | `McpConnectionInfo` 删除 `ScriptLanguage` 属性与 `scriptLanguage` 构造参数；`ToIni()` / `ToJson()` 不再写 `script_language`；`PublishConnectionInfo` 改用 `maxDetails:` 命名参数 |
| `McpServiceStatusWindow.cs` | 删除语言下拉框 `DrawScriptLanguageConfiguration()` 及调用；「复制技能安装提示词」只提 `SKILL.md` + `unity_bridge.js` + Node.js 18+，并说明 INI 只发布 `max_details` 等启动配置 |
| INI | 服务启动时不再写 `script_language`；客户端只读 `base_url` / `port` / `max_details`，旧 INI 残留字段被忽略（测试用例已覆盖） |
| 测试 | `McpDiscoveryTests` 改为断言 INI/discovery **不含** `script_language`；JS 用例改为「旧 INI 的 script_language 被忽略」 |

- 保留项：Unity 侧的 `python_runner` 工具是 MCP 能力（在编辑器里执行 Python 脚本），与技能客户端语言无关，**未删除**；`McpService.cs` 的 `.py` MIME 处理同理。
- `dist/unity3d-skill.zip` 重建：14 项 → **10 项**，包内无 `.py`，`SKILL.md` / `unity_bridge.js` / `test_unity_bridge.js` 与源文件字节一致，`testzip()=None`。

**3) 验证**

- `node --test tests/test_unity_bridge.js`：**64/64 通过**；`git diff --check` 通过；4 个改动过的 C# 文件词法括号未闭合 0 / 错配 0；`docs/index.html` 9 类标签开闭计数全等。
- 实机联调未做：当前 `localhost:8000/discovery` 返回 502，Unity 服务不在线。需重启 Unity（重新编译 C#）后再验：状态窗口无语言下拉、INI 不含 `script_language`、`schema hierarchy_create` 等描述为局部坐标/完整层级路径、`object_delete confirm` 为显式授权语义。重启前 `~/.unity-mcp/skill.ini` 仍残留旧 `script_language=js`，客户端会忽略，不影响使用。

---

## 一句话

把 Unity Skilled MCP 从「能按需发现」补齐到「能检索、能读正文、能批量执行」，补齐 7 个 Unity 侧专用工具、修复 Unity Console 反馈的编译错误；随后加入不生成 DLL 的运行时 C# 内存编译执行，并**整合进 `code_runner` 由 `mode` 显式选择，默认不编译**；最新一轮把两个客户端脚本改造成可 import 的库（原生参数 + 工作流上下文），并根治了 Git Bash 相对路由转换问题。

---

## 历史一轮：双语言脚本可作为库 import，并修复 Git Bash 相对路由（现已改为仅 JS）

**问题 1（修复）**：上一轮实测发现 Windows Git Bash / MSYS 会把 `request /groups` 的 `/groups` 自动转成 `C:/…/PortableGit/groups`，导致 URL 校验失败。原来只在 `SKILL.md` 写了规避说明，本轮在代码里根治：

- `unity_bridge.js` / `unity_bridge.py` 新增 `normalizeRequestTarget` / `normalize_request_target`。按环境变量 `EXEPATH`（指向 `<Git 根>/bin`）精确剥离根前缀，把转换后的本地路径还原成 `/ROUTE`；无法还原的盘符路径明确报错并给出提示，绝不当作 URL 访问。
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
node scripts/unity_bridge.js call code_runner --args '{"action":"probe","mode":"runtime"}'
node scripts/unity_bridge.js call code_runner --args '{"action":"execute","mode":"runtime","code":"return 1+1;"}'
node scripts/unity_bridge.js call code_runner --args '{"action":"execute","mode":"dll","code":"..."}'
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

改动文件：`scripts/unity_bridge.js`、`scripts/unity_bridge.py`、`tests/test_unity_bridge.js`、`tests/test_unity_bridge.py`、`SKILL.md`。

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
| `node --test tests/test_unity_bridge.js` | 57 / 57 通过（本轮 +9） |
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

---

## 本轮：TexGen 扩展「按 Mesh UV 绘制」（在原有 7 个动作基础上追加）

> 用户：「texgen 能针对指定 mesh 生纹理么，比如有个桌子模型」→ 明确「保持现有工具基础上扩展」。

**定位澄清**：texgen 是 demo 工程的自定义工具模块（`demo/Assets/Extends/CustomTools/TexGen/`），不是 `unity-skilled-mcp` 技能包的一部分；`SKILL.md` 只负责 discover/call 契约。原 `TgSpec` 只有画布字段，完全没有 mesh / UV / 材质输入，因此「数据给够就生效」不成立，必须加读 mesh 的能力。

### 新增

| 文件 / 动作 | 内容 |
| --- | --- |
| `TexGenMeshUv.cs`（新增，含 `.meta`） | UV 三角形栅格化成 coverage mask；按 `submesh` / `island`（顶点索引 + UV 接缝的并查集）/ `all` 分组；按区域合成多套指令表；边缘颜色外扩（padding）防 mip 接缝黑边 |
| `texgen.uv_info` | 返回 UV 分区、每区的 `rect_px`（可直接写进图层几何）、覆盖率、UV 超界警告 |
| `texgen.for_mesh` | 读 Mesh → 分析 UV → 每区渲染一张画布 → 按 mask 合成 → 写 PNG，可选 `material_path` + `texture_property` 自动挂材质 |

### 设计约定

- **不改动** `TgRasterizer`、原有 7 个动作及 `TgSpec`；每个区域仍走原来的 2D 画布渲染，UV mask 只做后处理合成。`TgParser.Parse` 复用，全局 `layers` 只渲染一次并被所有未覆盖的区域共享。
- 像素坐标与 UV 对齐：`x = u * width`、`y = v * height`，画布第一行为 `v = 0`；`rect_px` 已换算，调用方无需自行翻转 V。
- `mesh_path` 支持 `Assets/...` 资源（含 FBX 内嵌 Mesh，走 `LoadAllAssetsAtPath`）或场景 GameObject 层级路径（含 inactive 兜底、MeshFilter / SkinnedMeshRenderer / 子物体）。
- 未覆盖像素填画布底色；`padding` 默认 4 像素向外扩散颜色。UV 超出 [0,1] 会裁切并给警告（不自动 tile）。
- 材质挂载是显式副作用：只有传 `material_path` 才写，失败只记录 `material_error`，不影响已写出的 PNG。

**遗留**：UV 岛分组按顶点索引 + UV 值判连通，未做面积/法线驱动的 3D 投影（即「桌面横纹、桌腿竖纹」需靠 `regions` 手工分区），亦未做 AO / 曲率烘焙。

---

## 本轮：ProBuilder UV 图集扩展 + 红警2盟军坦克端到端验证

> 用户：「效果不好的话就扩展 probuilder 的 UV 功能，开放给技能，让模型 UV 适配贴图」。

**根因**：ProBuilder 图元默认每面 0–1 平铺 UV，`mergeObjects` 合并后所有部件的 UV 完全重叠，贴图必然串色。原模块 14 个动作没有任何 UV 编辑能力。

### 新增（纯新增，原有 14 个动作零改动）

| 项 | 内容 |
| --- | --- |
| `ProBuilderUvTools.cs`（+ `.meta`） | 同 `probuilder` 前缀的独立模块，动作名与原有 14 个不冲突，经 ToolCatalog 自动注册 → 出现在 `/tools`、`groups`、GUI，技能按 discover → list → schema → call 即可调用（SKILL.md 按铁律不写具体工具名） |
| `probuilder.uv_atlas` | 按「同位置顶点 + 三角形边」并查集求几何连通部件 → 每部件分配一格互不重叠的图集矩形 → 盒式投影（6 轴向各占子矩形）或平面投影 → 按 `(原顶点, 轴向组)` 重建顶点，跨组自动分裂消除 UV 接缝冲突 → 写回 `MeshFilter.sharedMesh`；默认 `bake=true` 移除 ProBuilderMesh，防止后续 `ToMesh/Refresh` 用 Auto UV 覆盖。返回每格 `rect` / `rect_px` / 三角形数 / 包围盒 |
| `probuilder.uv_info` | 只读查询 UV0 连通岛、像素矩形、覆盖率、跨岛重叠像素、UV 是否越界 |

**设计约定**：不依赖 ProBuilder 内部 UV API（`UVEditing.ApplyUVs/GetUVs` 是 internal），直接重建 `UnityEngine.Mesh`，任何读 `Mesh.uv` 的工具（含 `texgen.for_mesh`）都能消费。像素坐标与 texgen 对齐：`x=u*width, y=v*height`，第一行 `v=0`，`rect_px` 可直接写进图层几何。

### 实机验证（Unity 2022.3.62f3c1，MCP :8000）

- 新建独立场景 `Assets/RA2Allied/RA2Allied.unity`（不动 CannonBird 场景），6 辆盟军作战坦克：灰熊、多功能步兵战车、幻影、光棱、坦克杀手（德国）、战斗要塞；每辆 14–16 个 ProBuilder 图元合并为单网格，共 91 个部件。
- 每辆 `uv_atlas`：**图集格子数 = 部件数**（16/15/15/15/14/16），UV 连通岛 322–402 个，覆盖率 0.53–0.62，**跨岛重叠像素全部为 0**，UV 全在 [0,1] 内，无越界。
- `texgen.for_mesh` 按格子画分色图层（车体/炮塔/履带/轮子/炮管/棱镜/伪装/盟军星徽），生成 6 张各不相同的 512×512 PNG；6 个材质 `_MainTex` 均指向各自 PNG；场景 6 个 `MeshRenderer` 各自引用对应材质 guid。
- 全量 C# 编译（Unity 真实托管 DLL，Editor + TexGen + ProBuilder）**0 错误**；技能 JS 回归 **66/66**。

### 已知边界

- 几何是图元组合的低模，非 RA2 精灵级还原；未做 3D 位置/法线驱动图案、AO / 曲率烘焙。要更高还原度需 `createPolyShape` / 布尔 / 倒角做斜面车体与细节。
- 部件 → 格子的配色映射用几何包围盒中心就近匹配，个别部件可能串色；要精确应按材质或部件名分组，而非猜测 bounds。
- `texgen.for_mesh` 写 PNG 后触发纹理导入，会打断 Unity 主线程上的 HTTP 响应（客户端报「无效 JSON-RPC 响应」）；**写操作本身已生效**，须以只读手段核实产物再续行，勿重放。编排脚本已按此实现，并同步为 SKILL.md 的一条常见坑。

---

## 本轮：问题复盘、Mesh 资产化与贴图去重

> 用户：「遇到的问题总结一下，并优化。另外，生成的 mesh 数据，为何不是用的 ProBuilderMeshFilter，且 mesh 未存文件，切换场景不就丢了。最后，你生成的贴图文件里面有很多重复的图元」。

### 一、问题清单（按根因归类）

| # | 现象 | 根因 | 本轮优化 |
| --- | --- | --- | --- |
| 1 | 合并后贴图糊、串色 | ProBuilder 图元每面 0–1 平铺 UV，合并后全重叠 | 上轮已加 `probuilder.uv_atlas` |
| 2 | 写 PNG 后客户端报「无效 JSON-RPC 响应」，但 PNG/材质实际已写 | 一次写触发 3~4 次全量 `AssetDatabase.Refresh()`（写后、importer 设置、SelectAsset 各一次）+ Unity 文件监视器自动刷新 → 主线程域重载 → HTTP 响应与请求账本一起丢失 | 改单文件 `ImportAsset(ForceSynchronousImport)`；`DisallowAutoRefresh()` 包裹写操作、`EditorApplication.delayCall` 里恢复 `AllowAutoRefresh()`（避免同步恢复在响应写出前触发刷新）；`select_asset` 默认改 false、`SelectAsset` 不再 Refresh。6 辆实机中 4 辆响应恢复正常 |
| 3 | 编排把「响应丢失」误判为写成功 | `tolerant()` 吞掉所有异常，包括工具返回 `success:false`（例如找不到 GameObject），并且只检查 PNG 是否存在 → 旧文件被当成新写 | 区分业务错误（`MCP isError` / `success:false`）直接抛出，仅网络层异常容忍；写前记录 mtime，写后必须 mtime 变化才算成功 |
| 4 | `texgen.for_mesh` 报「未找到 GameObject 'RA2_Allied_Tanks/…'」 | `mesh_path` 绑定场景层级路径，域重载 / 父级变化后失效 | 改读持久化的 `.asset`；`uv_atlas` 也放宽为「仅 `bake=false` 才需要 ProBuilderMesh」，可对已 detach 的对象重跑 |
| 5 | 子对象创建时找不到刚建的父对象 | `hierarchy.create_empty` 与后续查找跨请求时序 | 编排统一挂到常驻根 `RA2_Allied_Tanks` |
| 6 | IFV 材质未挂、Mirage 落盘检查过早 | 异步生效 / 域重载 | 只读核实 + 缺则补挂 `_MainTex` |
| 7 | `TgParser` 默认画布 256 与 UV 布局默认 512 尺寸错位 | 两套默认值互不相干 | 强制把显式 width/height 写入解析 root；合成前校验 `Color32[]` 长度 |
| 8 | 218 图层渲染占满主线程 | `TgRasterizer` 对每个图层遍历整张超采样画布（ss=2 时 1024²）并分配 16MB 临时缓冲 | 用 1 个全局磨损层取代逐面 noise，装饰按面限量，图层 218 → 31~34；新增的 `nearest` 填充用 O(w·h) 多源 BFS，不额外增加每图层开销 |
| 9 | 响应丢失后 `status` 查到 `unknown` | 域重载会重建请求账本 | 不把 `unknown` 当「未执行」；以只读产物核实为准，绝不重放写操作 |
| 10 | mesh 不是 asset，且 detach 组件有销毁网格风险 | 见下节 | 见下节 |
| 11 | 贴图里满是重复图元 | 见第三节 | 见第三节 |

### 二、关于 ProBuilderMeshFilter 与「切场景就丢」

**先纠正前提**：ProBuilder 5.2.4 **没有 `ProBuilderMeshFilter` 类型**。它用标准 `MeshFilter` + `MeshRenderer` + `ProBuilderMesh` 组件，后者以 `[SerializeField] Mesh m_Mesh` 持有网格（`Runtime/Core/ProBuilderMesh.cs`）；`filter` 属性只是对 `MeshFilter` 的包装，并给它打上 `HideFlags.DontSave | HideInInspector | NotEditable`。所以代码操作 `MeshFilter.sharedMesh` 本身没错。

**「切场景就丢」并不成立**：Unity 会把运行时 `Mesh` 作为场景内嵌对象序列化进 `.unity`。改前实测 `RA2Allied.unity` 为 **597,148 字节**，含 6 个 `--- !u!43` Mesh 节点，`MeshFilter.m_Mesh` 指向这些场景内 fileID。只要场景被保存，切场景后网格仍在。

但真实缺陷有三个：内嵌 mesh 不能跨场景 / 预制体复用、场景文件被网格数据撑大；`bake=true` 的 `DestroyImmediate(ProBuilderMesh)` 会执行 `OnDestroy → DestroyUnityMesh() → DestroyImmediate(MeshFilter.sharedMesh, true)`，把刚写好的 UV 网格一起销毁；detach 后对象不再是 ProBuilder 可编辑对象，且 `DontSave` 标记需恢复。

**优化**：`probuilder.uv_atlas` 新增 `save_mesh`（默认 true）与 `mesh_path`（默认 `Assets/Generated/Meshes/<名称>_UvAtlas.asset`）。
- 用 `AssetDatabase.CreateFolder` 逐级建目录（不触发全量 Refresh）；`CreateAsset` 保存；若 asset 已存在则 `EditorUtility.CopySerialized` 覆盖内容以**保持 GUID 稳定**，避免已保存场景 / 材质引用断裂。
- detach 前设 `component.preserveMeshAssetOnDestroy = true`，销毁后把 `filter.hideFlags = HideFlags.None` 并重新绑定 asset Mesh。
- `EditorSceneManager.MarkSceneDirty`；编排末尾 `scene.save` 落盘。
- `probuilder.uv_info` 新增 `meshAssetPath` / `meshIsAsset` / `hasProBuilderMesh`，便于只读核实。
- 结果：6 个 `.asset`；场景 6 个 `m_Mesh` 全部变成 `{fileID: 4300000, guid: …, type: 2}`；内嵌 Mesh 节点 **0**；场景 **597KB → 23.9KB**；`uv_info` 6/6 `meshIsAsset=true`。

### 三、贴图里的重复图元

三个叠加根因：

1. **装饰按格子复制**：旧编排给每个 cell 都画 noise + 白条 + 星徽，6 个轮子 cell 各画一个大径向渐变圆，履带各画 6 条横线 → 十几个近乎相同的图元。
2. **标签靠猜**：`matchTags` 只用包围盒中心距离且强制一对一（`used` 集合），没有尺寸信息，小轮子常被错配成车体类。
3. **盒式投影切碎图案**：`box` 投影把每格拆成最多 6 个轴向子矩形，装饰却按整格拉伸 → 每个子面只显示图案的一块，出现「半圆、断线、缺角星」，看起来像一堆重复小图元。

**优化**：
- `uv_atlas` 返回 `face_rects`（每个实际使用的轴向面的 `rect` / `rect_px` / `axis_group`），贴图按面绘制而非按整格拉伸；底色仍按整格铺（纯色被切分不会出现碎片）。
- 装饰改成**按坦克限量**：盟军星徽 1、白条 1、棱镜高光 1、伪装 2；轮子改为小轮毂点；履带节纹 6 → 3。
- `matchTags` 改成「中心距离 + 包围盒尺寸」加权，且不再强制一对一。
- `texgen.for_mesh` 新增 `fill_mode`：`background`（旧行为）| `nearest`（O(w·h) 多源 BFS，把未覆盖像素填成最近 UV 区域颜色），消除一圈圈岛轮廓与 mip 黑边；未覆盖像素本来不会被网格采样，填充是安全的。
- 图层数 218 → 31~34，PNG 由 ~88KB 降到 ~9KB，贴图从「碎片 + 重复圆」变成完整分区色块。

### 四、验证

- 全量 C# 编译（Unity 2022.3.62f3c1 真实托管 DLL，Editor + TexGen + ProBuilder）**0 错误 / 0 警告**；技能 JS 回归 **66/66**；UV / padding / nearest 填充断言 **18/18**。
- Unity 实机 `:8000` 重跑 6 辆：6 个 `.asset`、6 张互异 PNG、6 个材质 `_MainTex` 全部挂载；UV 岛 322–402，跨岛重叠 **0**，越界 **0**；`meshIsAsset` **6/6 true**。
- 新增可复现编排 `docs/ra2_allied_build.js`（`--rebuild` 完整建模 / `--retexture` 复用现有网格 / `--only=<id>`），含写前 mtime 校验、业务错误区分、场景保存。

### 五、仍存边界

- 首次导入新 asset 时仍偶发丢响应（2/6），`status` 因账本随域重载清空返回 `unknown`；策略仍是「只读核实、不重放」，长耗时写建议走 `async_call` 门面。
- `TgRasterizer` 每图层全画布遍历未改（保持既有渲染行为不变）；图层数需控制在几十量级，后续可做按包围盒裁剪。
- 模型仍是图元组合低模，非 RA2 精灵级还原；装饰限量后贴图更素属预期，更高还原需按部件名/材质精确分组 + 3D 位置驱动图案。

---

## TexGen 表面引擎扩展（平铺 / 图案 / 域扭曲 / PBR / 模板库）

### 一、需求与判断

用户提问：「贴图生成框架 texgen 目前够强大了么，还能否继续完善实现更多应用场景，并提供更全面的内置模板」。

读完全部源码（7 文件 3602 行）后的能力盘点：8 种 SDF 图元、6 种填充、7 种混合、5 种噪声都是够用的；真正的短板集中在四个「贴图域必答但当时答不了」的问题上：

| 缺口 | 后果 |
| --- | --- |
| 噪声按绝对坐标采样，无周期 | 地面/墙面贴图左右边缘不连续，无法平铺 |
| 没有图案填充 | 砖墙/瓦片/木地板/织物只能靠手工堆 polygon，实际做不出来 |
| 没有 domain warp | 木纹、大理石、熔岩的纹理灵魂做不出来，只能出直线条纹 |
| 只输出单张图 + 法线 | roughness / AO / 金属度要人再跑一遍或手搓 |

### 二、实现

**1. 无缝平铺（`tiling`）**

`TexNoise` 新增全套 `*T` 周期化变体（`ValueT` / `PerlinT` / `FbmT` / `FbmPerlinT` / `RidgedT` / `WorleyT`），核心是 `WrapIndex(int, period)`：哈希取格点时按周期取模，边界两侧共享格点，因此左右上下严格连续而非「看起来接近」。`SampleNoise` 在 tiling 下把画布映射为整数格数（`u = x / width * nx`），即使 `width` 不能被 `scale` 整除也无缝。

图案层同样处理：`SnapCell` 把 cell 吸附为 `canvas / round(canvas / cell)`；tiling 时图案锚点从图层中心改为画布原点（`anchor = tiling ? 0 : center`），否则画布尺寸不是格高整数倍时会切出半行砖。

**2. 图案填充（`fill="pattern"`）**

新增 `TgPattern` 十种：`brick`（错缝砌筑）、`plank`（长条随机错缝 + 纵向微光）、`tile`、`checker`、`stripe`（占空比由 `stagger` 控制）、`hex`（平顶蜂窝）、`scale`（叠瓦鱼鳞）、`weave`（平纹经纬 + 圆柱高光）、`dot`、`cobble`（石砌/龟裂）。共用参数：`cell` / `gap` / `gapColor` / `stagger` / `variation` / `roundness` / `rotation`。`variation` 按格子哈希做亮度抖动，避免重复格子雷同。

三处需要定稿的取舍：

- **hex**：初版把 pointy-top 的中心网格配 flat-top 的距离函数，六边形之间出现三角黑洞（图已复现）。改为 flat-top 排布（列距 `1.5R`、行距 `√3R`、隔列半行偏移）+ `max(dx·0.866+dy·0.5, dy)/0.866` 距离，蜂窝严丝合缝。
- **scale**：单行半圆在行间留下大片黑三角。改为每像素同时比较「本行 + 上一行」两排鳞片，取最近圆心距离，`ry = v·0.75` 让圆弧探入下一行，形成真实叠压关系。
- **cobble**：`F2-F1` 在随机点分布下均值约 0.25，按 `gap/s` 直接比较会把所有石头缩成孤岛；阈值乘 0.4 才得到「多边形石块铺满 + 细缝」。中途试过 `F1 < radius` 的圆石判定，结果是一片互不相连的圆点，已回退。

**3. 域扭曲（图层级 `warp`）**

`WarpPoint` 用两个独立 seed 的噪声场偏移填充采样坐标（几何不受影响）：`p' = p + (n1-0.5, n2-0.5)·2·amount`。`stripe + warp` = 木纹，`noise + warp` = 大理石脉络。tiling 下扭曲场本身也周期化，保证扭曲后仍可平铺。

**4. PBR 派生（`texgen.pbr`）**

一次渲染后用同一份像素派生六通道并分别设置导入参数：`BaseColor`（sRGB）、`Height`（linear）、`Normal`（NormalMap）、`Roughness`、`AO`、`ORM`（R=AO / G=Roughness / B=Metallic，glTF / URP 打包约定）。`AO` 用八方向、两档距离的邻域最高点比较（避免 13×13 全核采样在 1024² 上爆炸）；`Roughness = bias + (0.5 - height)·contrast`，即凸起更光滑、凹陷更粗糙。可选 `material_path` 直接把 albedo + normal 挂到材质的 `_MainTex`/`_BaseMap` + `_BumpMap`（自动判属性、启 `_NORMALMAP`）。

**5. 模板库（`texgen.templates`）**

原来 4 个模板是硬编码 C# 字符串常量，扩不动。改为 `Assets/Extends/CustomTools/TexGen/Templates/*.json`（Unity TextAsset），`texgen.templates` 用 `AssetDatabase.FindAssets("t:TextAsset", folder)` 枚举，支持 `tag` 过滤与 `name` 精确取，返回的 `spec` 可整体投喂 `render` / `pbr`。共 **33 个**：地面 7、墙面 6、金属 3、织物 4、自然 5、UI 4、图标/特效 4。新增模板只加 JSON，不需改代码、不需重编译。

**6. 顺手修的既有缺陷**

`texgen.batch` 一次写多张 PNG 时没有 `DisallowAutoRefresh`，资源监视器中途刷新会重载域、切断 JSON-RPC 响应（`for_mesh` 早已处理，batch 漏了）。已给批量循环套 `DisallowAutoRefresh` / `AllowAutoRefresh`。

### 三、验证

Unity 2022.3.62f3c1 实机逐张渲染核对（`demo/Assets/CannonBirdDemo/Textures/TexGenTests/`）：

| 模板 | 结果 |
| --- | --- |
| `brick_wall` + `texgen.pbr` | 错缝砖行、砂浆缝、砖面色差正确；上下边缘为完整砖行；6 通道全部落盘，法线图呈砖面凸起 + 缝隙凹陷 |
| `hex_panel` | 蜂窝严丝合缝、左右闭合 |
| `cobblestone_path` / `stone_wall` | 多边形石块铺满、细缝 |
| `fish_scale` | 叠瓦关系成立 |
| `marble_stone` | 宽脉络 + 细纹交叉 |
| `lava_flow` | 黑岩壳 + 橙色发光裂缝 |
| `ui_panel` / `crosshair` / `linen_weave` | 圆角卡片、对称准星、平纹织物均正常 |

编译 `error_count = 0`。模板 JSON 33/33 通过 `JSON.parse` 与必需键校验。

**无缝性的客观验证**：把成品贴图按 2×2 拼回一张大图（`*_tiled2x2.png`），直接看中央十字接缝。`brick_wall` 与 `hex_panel` 拼接后砖行/蜂窝连续、无错位。两个必须同时满足的条件是：

1. **格子索引也要按周期取模**（`JitterWrapped`）。跨边界的同一块砖从两侧各被访问一次，若 `col` 不 wrap（0 vs cols），同一块砖会拿到两个抖动颜色，接缝处就是一道色阶。这是「几何对齐但颜色不连续」的隐蔽坑。
2. **错缝图案需要偶数周期**。`brick` 的交替错缝行数必须为偶数，`hex` 的隔列半行偏移要求列数为偶数，否则跨边界时 offset 翻转，接缝处直接错位半格。`hex` 还额外把行数取整并改用以 `hw/vh` 归一化的距离函数（`HexDistanceNorm`），这样两个方向都能闭合，代价是格子被拉伸几个百分点。

早前用「首末列像素差 / 内部相邻列像素差」做量化，对噪波类有效（`grass_field` 0.77、`wood_grain` 0.97），但对砖缝/蜂窝这类本来就有硬边的图案会误判（18×），最终改用 2×2 拼接目视确认。

### 四、仍存边界

- `texgen.batch` 单次超过 ~15 秒（约 6 张 512² 以上）仍会被客户端默认超时打断；写操作已完成，**须只读核实产物、勿重放**。批量建议分批 ≤5 张，或用 `--timeout` 放在子命令之前。
- `hex` 在 tiling 下靠「列数取偶 + 行数取整」闭合两轴，格子会有几个百分点的各向异性拉伸；带 `rotation` 的图案（含斜纹 `denim`）无法保证严格无缝，因为旋转后的周期不再与画布轴对齐。
- `wood_grain` 与 `marble_stone` 本质是「条纹 + 强扭曲」，能出直纹木与脉络大理石，但做不出年轮、树节这类拓扑特征；要更真需引入外部位图采样（当前渲染器不支持读图）。
- 图案仍走「每图层全画布遍历」，`hex` 每像素 9 次邻域查询，512² 约 2.3 秒；2048² 会明显变慢。

## 本轮：UISourceGen —— SkiaSharp UI 控件图 + 布局 JSON 引擎

> 用户：「我还需要一套生成 ui 控件的工具，希望集成 SkiaSharp 库，然后提供一套类似 texgen 的工具系统，供 mcp 和技能访问」；「我要的是生 ui 控件图以及 ui 界面对应的布局 json 即可，可以叫 UISourceGen」

### 一、定位与边界

TexGen 画的是「贴图」（像素级程序化纹理，最终是材质输入）；UISourceGen 画的是「控件」（可直接进 Unity UI 的切图 + 摆放数据）。两者共用同一套 MCP 工具形状（`spec` / `examples` / `templates`），但渲染后端换成 **SkiaSharp**：TexGen 自己写光栅化，UISourceGen 用成熟的 2D 矢量 + 文字引擎，代价是引入一个原生依赖。

**只做两件事，不做 UGUI 生成**：输出控件图 PNG + 对应 `layout.json`。不在场景里建 GameObject、不挂 Image/Button 组件 —— 用户明确要「图 + JSON」，把「怎么用」留给调用方。

### 二、依赖

SkiaSharp 2.88.9 vendored 进仓库，不走 NuGet 还原：

| 文件 | 来源 | 说明 |
| --- | --- | --- |
| `demo/Packages/com.unity-skilled.skiasharp/SkiaSharp.dll` | NuGet `skiasharp/2.88.9/lib/netstandard2.0/` | 托管程序集，全平台 |
| `demo/Packages/com.unity-skilled.skiasharp/Plugins/x86_64/libSkiaSharp.dll` | NuGet `skiasharp.nativeassets.win32/2.88.9/runtimes/win-x64/native/` | 原生库，`.meta` 里 PluginImporter 限定 `Win + x86_64`，其他平台 `enabled: 0` |

包的 `SkiaSharp.dll.meta` 是普通 Managed Plugin，原生库用 `platformData` 关掉 `Any`、只开 `Win/x86_64`，避免 Unity 把它当 AnyCPU 打进 Linux/Android 包。`manifest.json` 加 `"com.unity-skilled.skiasharp": "file:com.unity-skilled.skiasharp"`（embedded 包本可自动发现，写进去是为了让依赖关系显式可见）。

### 三、文件

`demo/Assets/Extends/CustomTools/UISourceGen/`（4 源，**无 asmdef**——本项目 Assets 下统一编进 `Assembly-CSharp`，加 asmdef 反而会把 `UniMcp` 引用切断）：

| 文件 | 职责 |
| --- | --- |
| `UiGenModel.cs` | 纯数据：`UiControlType`（16 种）/ `UiState`（6 态）/ `UiAnchor`（9 锚点）/ `UiColor`（hex、`r,g,b,a`、数组三种解析）/ `UiStyle`（含 `Merge`/`Clone`）/ `UiTheme` / `UiControl` / `UiScreen` |
| `UiGenRenderer.cs` | 纯 .NET + SkiaSharp，**零 Unity 依赖**，因此能独立跑验证。`Surface()` 一个原语包办阴影+渐变+圆角+描边；`Text()` / `TextWithIcon()` 自带缩放适配；`DrawIcon()` 内置 12 个矢量图标；`RenderControls` / `RenderControl` / `RenderScreenPreview` |
| `UiGenParser.cs` | 容错 JSON → 模型。每个字段都有 fallback，坏值只记 warning 不抛 |
| `UiGenTool.cs` | MCP 模块，5 个动作 + 落盘 + `AssetDatabase` 导入 |

### 四、动作

工具分两层。**控件层**把常见 UI 部件一键成图；**画布层**开放 SkiaSharp 的全部 2D 绘制能力，任何图都能拼。两层共用 `uisourcegen` 前缀与同一个模块实例。

**控件层**（5 个）：

| 动作 | 参数 | 产出 |
| --- | --- | --- |
| `uisourcegen.render` | `screen`(require) `output_dir` `base_name` `preview` `supersample` `select_asset` | 每控件 × 每状态的 PNG + `preview.png` + `layout.json` |
| `uisourcegen.batch` | `screens`(require) `output_dir` `preview` `supersample` | 多界面各自一个子目录 |
| `uisourcegen.spec` | — | 语法骨架 + 16 种 `control_types` + 图标名列表 |
| `uisourcegen.examples` | — | 一份可直接用的登录界面 spec |
| `uisourcegen.templates` | `tag` `name` | 读 `Templates/*.json`，返回原文供直接投喂 `render` |

**画布层**（7 个）：

| 动作 | 参数 | 产出 |
| --- | --- | --- |
| `uisourcegen.draw` | `canvas`(require) `output_dir` `base_name` `format` `quality` `name_suffix` `supersample` `select_asset` | 一张图（png/jpg/webp/bmp/gif）+ `canvas.json` 回写 |
| `uisourcegen.draw_batch` | `canvases`(require) `output_dir` `format` `quality` `supersample` | 每张画布一个子目录 |
| `uisourcegen.pdf` | `canvases`(require) `output_dir` `base_name` | 多页 PDF |
| `uisourcegen.svg` | `path`(require) `output_dir` `base_name` `width` `height` `fill` `stroke` `stroke_width` `padding` `supersample` | SVG 路径按包围盒自动定尺寸栅格化 |
| `uisourcegen.measure` | `text` `path` `font_size` `font_path` `font_family` `bold` `text_scale_x` | 文本宽度/上下降部/行高，路径长度与包围盒 |
| `uisourcegen.fonts` | `filter` | 系统字体族列表 + 默认字体路径 |
| `uisourcegen.canvas_spec` / `uisourcegen.canvas_examples` | — | 画布语法骨架 / 可直接投喂的示例 |

默认给交互控件（button/toggle/checkbox/radio/tab/slider/dropdown/input）出 `normal` + `hover` + `pressed` + `disabled` 四态；非交互控件（panel/card/label/icon/divider/progressbar/scrollbar/badge）只出 `normal`。`states` 里显式写到的状态额外追加。

### 五、这轮踩到并修掉的设计缺陷

初版跑通但**排版全错**，逐张核对预览图后才定位到三个真问题，都不是「渲染 bug」而是**模型语义没定清楚**：

**1. 9 个锚点只实现了 `center`**
`Left()` 原本只写 `Anchor == Center ? X - W/2 : X`，于是 `topright` / `bottomleft` 当普通左上角算。HUD 的血条设 `bottomleft` + `y=660` 直接被画到 720 高的画布外，齿轮按 `topright` + `x=940` 落在屏幕中间。
改成 `Left(screenW)` / `Top(screenH)`：右列锚点 `X` 表示**距右边缘的间隙**（`screenW - X - W`），下排锚点 `Y` 表示**距下边缘的间隙**（`screenH - Y - H`），中间行/列按中心。这样同一份 JSON 换 `width/height` 仍然贴边，而不是绝对坐标写死。

**2. 字号不随控件走**
下拉框 `38` 高配主题 `font_size: 22`，`English` 加 `chevron` 直接顶出边框。`MakeTextPaint` 加 `boxH` 参数，字号上限 `boxH * 0.58`；`Text()` 里再按可用宽度等比缩一次（下限 0.6 倍），最后才 `FitText()` 加省略号。徽章更极端（`h=28` 装 `1200`），单独把上限压到 `h * 0.52`。改完 `1200` / `800` / `650` 全部完整显示，无省略号。

**3. 轨道色和填充色没有独立语义**
初版进度条把 `background` 当槽、`background_end` 当填充，于是「深红槽 + 亮红填充」被渲染成整个条都是渐变。补 `track_color` / `fill_color` / `fill_color_end` 三个样式令牌：槽恒为纯色 `FillRoundRectColored`，只有填充走 `FillRoundRectGradient`。滑块、开关、滚动条同样受益。

顺带修的两处：`Surface()` 里 `MaskFilter` 用了 `blur` 而非 `blur * 0.5`（阴影过散）；`SKTextAlign` 被误写成返回 `float` 的辅助函数（编译不过，`ToSkAlign` 改为返回 `SKTextAlign`）。

### 六、验证

**双轨验证**，因为这台机器有 Unity 但没在编辑器里点开：

1. **渲染正确性** —— 临时 dotnet 工程 `uisrc_test` 引用真实 `UiGenModel/UiGenRenderer/UiGenParser` + 一份 `UniMcp` JSON stub，逐模板渲染并做结构断言（frame 必须落在图内、PNG 不能为空、9-slice 不超过 frame 的 1/3）：

```
hud        ok   24 images
login      ok   25 images
mainmenu   ok   28 images
settings   ok   44 images
shop       ok   24 images
ALL CHECKS PASSED
```

2. **Unity 侧可编译性** —— 临时工程 `uigen_unity` 引用 **真实 MCP 框架源码**（`ToolModuleBase` / `ToolAttributes` / `ToolContext` / `MethodKey` / `Response` / `SimpleJson` / `LocalizationHelper`）+ **真实 Unity 2022.3.62f3c1 的 `UnityEngine.dll`、`UnityEngine.CoreModule.dll`、`UnityEditor.dll`** + 真实 `SkiaSharp.dll`，编译 UISourceGen 四个源文件：**0 错误 0 警告**。这一步抓到了 `Response` 实际在 `UniMcp.Models` 命名空间（初版漏 `using`）、`GetString` 少传 `fallback` 参数、`SKFont.GetMetrics` 应为属性等一堆在 Unity 里才会报的错。

产物留在 `docs/uisourcegen_smoke/templates/<模板>/{控件_状态.png, preview.png}`。

### 七、模板库

`Templates/*.json`（5 个，结构即 `render` 的 `screen` 入参，可直接投喂）：

| 模板 | 画布 | 控件 | 要点 |
| --- | --- | --- | --- |
| `login` | 960×600 | 10 | 卡片 + 标题/副标 + 双输入 + 开关行 + 主按钮 + 链接 + 语言下拉 |
| `settings` | 1024×720 | 17 | 面板 + 三 Tab（选中态带底部高亮条）+ 三条 slider 行 + toggle + dropdown + 顶/底分隔线 + 双按钮 |
| `hud` | 1280×720 | 12 | 四角布局：左下血/蓝条 + 弹药徽章，顶部居中分数/连击，右上小地图 + 齿轮，右下三个圆形按钮 |
| `mainmenu` | 1280×720 | 10 | 大标题 + 分隔线 + 四枚堆叠按钮（Play 主色/Quit 危险色）+ 右上齿轮+语言 |
| `shop` | 1100×740 | 15 | 双商品卡（卡内标题/描述/价格徽章/Buy）+ 币值徽章 + 关闭 + 竖向滚动条 |

模板里的坐标全部按真实锚点语义书写（右/下锚点写「间隙」），所以改 `width/height` 后仍然贴边。

### 八、仍存边界

- **只出图不出 UI**。`layout.json` 给了 `id/type/anchor/rect/text/icon/value/checked/selected/image/slice/states`，但把它变成 `RectTransform` 的活留给调用方。`slice` 是 `[L,T,R,B]`，可直接喂 `Image.Type.Sliced` + `sprite.border`，但**没有自动写 `.meta` 的 sprite border**——导入设置仍需人工或另写一段。
- **SkiaSharp 原生库只带了 win-x64**。macOS / Linux 编辑器要补对应 `runtimes/*/native/libSkiaSharp.dylib|so`，否则 `SKSurface.Create` 会在目标平台抛 `DllNotFoundException`。
- **单行文字**。没有换行、没有富文本、没有字距调整；超长文本靠缩小 + 省略号兜底。多行段落得拆成多个 `label`。
- **图标是手写矢量**。12 个内置图标（check/cross/arrow/chevron-down/star/menu/play/plus/minus/search/gear/heart）够做原型，不够做正式图标集；真图标建议走 `texgen` 出图或直接引 SVG。
- **没有九宫格拉伸的实际验证**。`slice` 数值按「圆角 + 描边、不超过 1/3」算出并断言过，但没在 Unity 的 `Image.Type.Sliced` 里实拉一次看边缘。

### 九、画布层：把 SkiaSharp 的全部 2D 能力开放给 MCP

控件层只能画「已经想好的 16 个部件」。要真正把 SkiaSharp 用起来，需要一层通用的、声明式的绘图 DSL —— 让 Agent 用 JSON 描述任意 2D 图形，而不是每次都新增一个控件类型。

#### 9.1 图元（16 种 `shape`）

| shape | 几何语义 |
| --- | --- |
| `rect` / `roundrect` | `x,y,w,h` 矩形；roundrect 另取 `radius` |
| `circle` / `oval` | `x,y` 是**外框原点**，`w,h` 是直径；也可只给 `radius` |
| `line` | `x,y` 起点 + `to:[dx,dy]` |
| `arc` | 外框 + `start_angle` + `sweep`；有填充时走 `useCenter` 扇形，只有描边时走弧线 |
| `path` | `path` 字段给 SVG path data，或 `points` 给局部点列 |
| `polygon` / `polyline` / `points` | 局部点列 + `x,y` 整体偏移 |
| `text` / `textOnPath` | 有 `w,h` 时按盒子对齐（水平三种 + 垂直居中），无盒子时 `x,y` 即基线 |
| `icon` | 12 个内置矢量图标 |
| `image` / `imageNinePatch` | 项目内或绝对路径图片，可选九宫格 |
| `vertices` | 逐顶点着色的渐变网格（可选 `indices`） |
| `group` | 容器，向下继承 transform / clip / paint |

**所有点列都是局部坐标**：`x,y` 平移整个元素。这个决定是必须的 —— 初版把 `points` 当绝对坐标，导致一个多边形无法整体挪位置，只能改每一个点。

#### 9.2 填充（`fill` / `stroke`）

8 种 paint：`solid`、`linear`、`radial`、`sweep`、`twoPointConical`（`conical` 别名）、`noise`、`image`、`none`。每个 paint 同时携带：

- 颜色：`color` / `color2` / `stops:[[offset,"#hex"],...]`
- 几何：`from`/`to`/`x0,y0,x1,y1`/`radius`/`start_angle`/`end_angle`
- 噪波：`base_freq`/`octaves`/`seed`/`turbulence`/`stitch`
- 描边：`stroke`/`stroke_width`/`stroke_color`/`cap`/`join`/`miter`
- 混合与抗锯齿：`blend`（28 种 SKBlendMode）/ `antialias` / `dither`
- 模糊：`blur` + `blur_style`（Normal/Solid/Outer/Inner）
- 图像滤镜：`filter` = `blur`/`dropshadow`/`offset`/`dilate`/`erode` + `sigma_x/y`/`dx`/`dy`/`radius`/`shadow_color`
- 路径效果：`path_effect` = `dash`/`corner`/`discrete`/`trim` + `dash`/`dash_phase`/`trim_start`/`trim_end`
- 着色器矩阵：`shader_matrix`（6 或 9 个数）
- 坐标系：`space` = `object`（默认，锚点相对图形框）/ `canvas`（画布坐标）

`space` 是这轮补的：裁剪遮罩里想让渐变铺满整块遮罩，就必须用画布坐标；而普通图形又希望渐变跟着图形走。两种都要，所以做成显式开关而不是猜。

#### 9.3 变换 / 裁剪 / 合成

- 变换：`translate` → `rotate` → `scale` → `skew` → `matrix`，围绕 `pivot_x/pivot_y`（默认元素中心）。给 `matrix` 时其余全部跳过。
- 裁剪：`clip:{shape,rect|radius,even_odd}`。**几何字段独立**（`ClipX/Y/W/H`），不覆盖元素自身的 box —— 初版复用了 `X/Y/W/H`，给一个图形加裁剪会顺手把它挪走。
- 合成：`layer:{opacity,blend}` 走 `SaveLayer`；裸 `opacity` 也走 SaveLayer（整棵子树统一透明度，而不是每个图元各自乘一次）。

#### 9.4 编码与文档

`draw` 支持 png / jpg / webp / bmp / gif，`quality` 对损率格式生效。`pdf` 走 `SKDocument.CreatePdf` 出多页文档。两张画布 800×530 + 860×480 的 PDF 是 12 MB —— PDF 里文字和渐变都是矢量对象，体积主要来自逐顶点网格，合理。

`draw` 每次都写一份 `canvas.json` 回写：把解析后的模型序列化成同一套 JSON。这不是日志，是**可重放的产物** —— 消费方拿它就能重建完全相同的图，也顺便当了解析器的往返测试。

#### 9.5 这轮修掉的 7 个真实缺陷

全部是**看着图才发现的**，单测数字都不会报错：

**1. `line` 的终点是绝对坐标**
`to:[dx,dy]` 文档写的是相对，代码里直接 `DrawLine(e.X, e.Y, e.X1, e.Y1)`，一条 210px 的短线画成了横穿整个画布的长斜线。

**2. `polygon` 填充被静默丢弃**
`SKCanvas.DrawPoints` 只能描边（`SKPointMode.Polygon` 是"连成折线"不是"填充多边形"）。实心三角形被画成三条边。改成检测到 `polygon` 就走 `SKPath` + 独立 fill/stroke 两遍。

**3. SVG path 不吃 `x,y`**
`ParseSvgPathData` 出来的路径停在原点，加 `Offset(e.X, e.Y)` 才跟别的图元一致。

**4. 顶点网格不吃 `x,y`**
同上，`vertices` 也补了偏移。

**5. 描边文字完全不可见**
`fill:{"color":"#00000000","stroke":true,"stroke_width":2,"stroke_color":"#4ecdc4"}` —— 颜色透明、描边色在 `stroke_color` 里。原来的 `MakeTextPaint` 用 fill 的 `Color`，于是画了个全透明的字。修法：把「描边藏在 fill paint 里」这种写法在解析期拆成独立的 `stroke` paint，并且文本绘制改成**先描边后填充**两遍。

**6. `SKAutoCanvasRestore(cv, false)` 是空操作**
构造传 `false` 就不 Save，`Dispose` 里 `_count >= 0` 永远不成立 —— 一个自称"自动恢复"的结构什么都没做。改成 `true`。

**7. `SKTileMode` 不存在**
SkiaSharp 2.88.9 里叫 `SKShaderTileMode`。这类错误只在真编译时才现形。

顺带：`MakeShader` 在裁剪组里坐标基准混乱 → 引入 `space` 开关；`ReadClip` 不再覆盖元素几何。

#### 9.6 验证

`docs/uisourcegen_smoke/canvas/` 下 8 张产物 + 1 个 PDF，由 8 份文档驱动：

| 文档 | 覆盖 | 断言 |
| --- | --- | --- |
| `shapes.png` | 10 种图元 + x/y 偏移 | 278 色，尺寸 = 声明值 |
| `gradients.png` | 4 种渐变 + 多 stop + 分形噪波 + 湍流 | 9081 色（渐变确实在插值） |
| `effects.png` | 投影/模糊/混合/虚线/trim/discrete/透明度/旋转组/圆与圆角裁剪 | 897 色 |
| `text.png` | 标题/中文/渐变字/描边字/盒子对齐/路径文字/投影字 | 373 色 |
| `icons.png` | 12 个内置图标 | 全部可辨识 |
| `mesh.png` | 逐顶点着色的三角形与四边形 | 22340 色 |
| `composite.png` | 图片缩放 / 图片作平铺着色器 / 图片 screen 混合叠加 | 1461 色 |
| `gradients.jpg/.webp` | 有损编码器 | 均可解码 |
| `multi_page.pdf` | 多页 PDF | 有 `%PDF` 头，2 页 |
| roundtrip | 6 份文档解析 → 序列化 → 再解析 | 6/6 几何与 shape 完全一致 |

同时 Unity 侧编译（真实 MCP 框架源码 + 真实 Unity 2022.3.62f3c1 程序集 + 真实 SkiaSharp.dll）覆盖全部 **9 个源文件**：**0 错误 0 警告**。

#### 9.7 边界

- **没有 SVG 文件解析**。只有 `SKPath.ParseSvgPathData`（路径数据），不解析 `<svg>` 文档、不做 `<use>`/`<style>`/CSS。要完整 SVG 得引 `Svg.Skia`。
- **没有 SkSL 运行时着色器**。`SKRuntimeEffect` 在 2.88.9 里存在，但没有开放；复杂效果只能拼内置 shader + 滤镜 + 混合。
- **没有 GPU 后端**。全程 CPU 光栅化，`GRContext` 完全没碰。
- **噪波只能单色调制**。`CreatePerlinNoise*` 产出灰度，我用 `Modulate` 叠一层颜色，所以做不出双色噪波图；要双色得用 `ColorFilter.CreateTable` 手搓映射。
- **PDF 里逐顶点网格会膨胀**。矢量化的三角网格每个三角形都是独立填充，12 MB 的 PDF 主要是它。

### 9.8 Unity 侧运行时：三个导致「渲染失败」的真实故障

dotnet 临时工程里全部通过，不等于 Unity 里可用。在真实 Unity 2021.3.45f2c1 编辑器里跑 `uisourcegen.draw` 时连续踩到三个独立缺陷，全部已修。

**故障一：原生插件平台键无效，`libSkiaSharp` 从未被加载。**
现象：`渲染失败：The type initializer for 'SkiaSharp.SKImageInfo' threw an exception.`，内层是 `DllNotFoundException: libSkiaSharp`；`Get-Process` 的 `Modules` 里找不到该模块。
根因：`Plugins/x86_64/libSkiaSharp.dll.meta` 的 `platformData` 写了 `Win:`，而 Unity 认的键是 `Editor: Editor` / `Standalone: Win64`。`Win` 是无效键被静默忽略，`Any` 又被 `enabled: 0` 关掉，于是插件在所有平台都不导入，`DllImport` 自然找不到库。
修法：平台条目改为 `Any: 0` + `Editor: Editor`（`CPU: x86_64`）+ `Standalone: Win64`，随后 `AssetDatabase.ImportAsset(..., ForceUpdate)` 重导入。
排错要点：`TypeInitializationException` 在 AppDomain 内**永久缓存**，首次失败后所有后续调用都只是重放旧异常；改完必须触发域重载（`engine.recompile`）才能重新验证，否则会误判「改了没用」。
附：`libSkiaSharp.dll` 的导入表只有 `ole32 / FONTSUB / USER32 / KERNEL32`，**没有 MSVC 运行库依赖**（CRT 静态链接），所以「缺 VC++ 运行库」不是本故障原因；两处二进制的 md5 与 NuGet 官方 2.88.9 win-x64 完全一致。

**故障二：`ProjectPath` 前缀偏移错一位，产物写到盘符根目录。**
现象：工具返回 `success: true` 且 `image: Assets/UISourceGen/x/x.png`，但工程里没有文件；实际落在 `E:/UISourceGen/x/x.png`。
根因：`assetsPath.Substring(6)` 切掉了 6 个字符，而 `"Assets/".Length == 7`。剩下部分以 `/` 开头，`Path.Combine` 视其为**根路径**并丢弃前面的 `Application.dataPath`，于是拼出 `/x/x.png`，最终写到当前盘符根。
修法：改为 `Length > 7 ? Substring(7) : ""`，并加注释说明为什么不能切 6。

**故障三：`[ToolAction]` 参数串用竖线拼接，只暴露第一个参数。**
现象：`uisourcegen.draw` 的 `inputSchema.properties` 只有 `canvas`，`output_dir` / `format` / `quality` / `supersample` 等全部不可见；`measure` 连 `text` 都没有，schema 为空。
根因：`[ToolAction("draw", "canvas=require|output_dir|format|...")]` 把一串参数当成了 `params string[]` 的**单个元素**。`ToolActionAttribute` 只按数组元素拆分，不按 `|` 拆；框架 `BuildSpec` 拿整个 `"canvas=require|output_dir|..."` 去 `KeyIndex` 里查，只匹配不到前缀之外的名字，最终只解析出 `canvas`，其余静默丢弃。
修法：全部改为每参数一个字符串元素，如 `[ToolAction("draw", "canvas=require", "output_dir", "base_name", "format", "quality", "name_suffix", "supersample", "select_asset")]`。13 个动作逐条改完，`templates` 另补注册此前缺失的 `name` 键。

**回归结果（Unity 2021.3.45f2c1，真实编辑器进程内）**

| 动作 | 参数暴露 | 实测 |
| --- | --- | --- |
| `render` | 6 项，`screen` 必填 | 3 控件 → 7 文件（含 4 态切图 + `layout.json`）|
| `batch` / `draw_batch` | 4 / 5 项 | 2 画布批量落盘 |
| `draw` | 8 项，`canvas` 必填 | 720×420，渐变/径向/锥形/文字/阴影/路径全对 |
| `pdf` | 3 项 | 2 页，12.4 MB，`%PDF` 头有效 |
| `svg` | 9 项 | 路径栅格化 300×100 |
| `measure` | 8 项 | 文本 153.75×42.23、路径长 288.68 |
| `fonts` | 1 项 | 282 族，`default_path` 正确 |
| `templates` / `examples` / `canvas_examples` / `spec` / `canvas_spec` | 2 / 0 / 0 / 0 / 0 | 均返回可用内容 |

证据图见 `docs/uisourcegen_smoke/unity_env/`：`canvas_full.png`（画布层全能力）、`controls_preview.png`（控件层整屏合成，含中英文文字）。

## 本轮：ToolModule 能力标签补全 + Unity MCP Services 工具集行尾展示标签（未提交）

- 65 个 `[ToolModule]`（unity-package 55 + demo 10）全部补 `Tags = new[] { ... }`，按 prefix 统一成 54 套受控标签、93 个 slug 标签；与 `[ToolAction].Tags` / `[CapabilityTags]` 一起经 `CapabilityTagsAttribute.Normalize` 合并，框架另自动补 group / prefix 两个默认 tag。
- **按 prefix 统一是硬约束**：`/groups` 的 tags 反查索引在同 prefix 内标签不一致时会退化成逐条 `"@工具全名"`；统一后实测 145 个 tag、271 个 prefix 引用、`@全名` 0 条，目录仍保持最紧凑形态。
- 显式剔除与自动 group/prefix tag 重复的项（`animation` / `hierarchy` / `texgen` 等不再重复声明），每模块至少 2 个标签；`prefix.action` 无冲突，标签全部已 slug 化、无重复。
- `McpServiceGUI.cs`：**只有工具集（模块）header 显示标签**，放在该行**行尾**（`Foldout` 之后用 `GUILayout.FlexibleSpace()` 撑开，标签右对齐固定宽 240px，`moduleStyle.clipping = TextClipping.Clip` 防止长标题溢出覆盖）；具体工具 header、工具展开区、内置门面工具均不显示标签。模块标签取该前缀下所有 `ToolSpec.Tags` 的交集，超过 3 个以 `+N` 收尾，tooltip 给出完整列表。`EditorGUILayout.Foldout` 无 `GUILayoutOption` 重载，故不能用 `GUILayout.Width` 约束标题宽度。
- **用户后续收敛**：具体工具一律不显示 tag，标签只保留在工具集（模块）行且必须在**行尾**。已移除 3 处工具级绘制（工具 header、工具展开区、内置门面工具），模块行改为 `Foldout → FlexibleSpace → 右对齐标签`；标签数据（`ToolModule.Tags` → `ToolSpec.Tags` → `/groups` 索引）不变。
- 验证：运行态 `GET /groups` 已读到 `asset-database` / `texture-generation` / `logging` / `ui-authoring` / `mesh-editing` / `uv-mapping` 等新 tag（145 个 tag / 271 个 prefix 引用 / 0 条 `@全名`），说明 Unity 已重载程序集；`git diff --check` 通过，`McpServiceGUI.cs` 词法括号未闭合 0 / 错配 0。未 commit/push。

## 本轮：多客户端并发冲突修复（未提交）

- 复核修正：`resources/subscribe` / `unsubscribe` 分发分支早已注释、`initialize` 声明 `subscribe: false`，`clientCallbackUrl` 与 `subscribedResources` 只被这段停用路径消费，**不是活跃的多客户端冲突**，本轮不动该路径。
- `McpService.cs` 新增写执行门：`EnqueueTask` 拆出 `EnsureQueuePumpRegistered`；`tools/call` 改走 `EnqueueWrite(Action<int>)`，由 `PumpWriteGate` 在 `EditorApplication.update` 中按代次启动，工具回调 `finally` 调 `ReleaseWriteGate`。HTTP 提前返回 `running` 后门仍持有，其他客户端写调用只排队不交错；5 分钟看门狗防工具不回调导致永久停摆，`Stop` / `ForceStop` 调 `ResetWriteGate` 作废旧代次。
- `ProcessMessageQueue` 从每帧 1 个改为每帧批量最多 16 个普通任务；注销 update 前用 `HasPendingWriteWork()` 复核写门状态，避免新入队写调用丢失泵。`HandleToolsCall` 的 `TaskCompletionSource` 加 `RunContinuationsAsynchronously`，避免主线程内联执行响应构建。
- `RuntimeMcpService.cs`：原来 `toolMethod.ExecuteMethod` 在 HTTP 工作线程直接跑（非主线程碰 Unity API），改为 `EnqueueToolExecution` + `Update()` 主线程泵，同样按代次串行；`Task.Run(() => tcs.Task.Wait(5000))` 换成 `Task.WhenAny` + `Task.Delay`，不再白占线程池线程。`DiscoverTools` / `HandleToolsList` / `RegisterTool` / `GetToolCount(s)` 用 `lockObj` 保护字典，消除多客户端并发 initialize 时的字典并发写。
- 行为变化：Runtime 服务现依赖 `Update` 驱动；编辑器暂停时工具请求会在 5 秒超时返回错误（原实现会在后台线程执行但线程不安全）。写操作吞吐由「每帧一个」变为「一个工具完整完成后才放行下一个」，同步完成的工具可在同帧连续放行。
- 文档同步：`README.zh-CN.md` 新增「8. 多客户端访问」、`README_MCP_HTTP.md` 特性与性能、`SKILL.md` 多步访问、`Unity3d_MCP_用户使用指南.md` Q4。
- 验证：`git diff --check` 通过；两个 C# 文件改动前后大括号增量一致（Editor 0→0，Runtime -2→-2），新增标识符引用已逐项核对。**未在 Unity 内编译/跑测试**（本机编译器调用被安全策略拦截），需在 Unity 编辑器重载后跑 `McpProtocolSafetyTests` / `WorkflowContractTests` 并实测多客户端并发。未 commit/push。

## 本轮：skill.ini 携带 Unity 版本与渲染管线（未提交）

- 需求：创建 `~/.unity-mcp/skill.ini` 时带上 Unity 版本号与渲染管线类型。
- `McpDiscovery.cs`：`McpConnectionInfo` 增 `UnityVersion` / `RenderPipeline`（构造函数可注入，未传则自动探测）；`PublishConnectionInfo()` 在服务启动成功（主线程）时传入 `Application.unityVersion` 与 `DetectRenderPipeline()`；`ToIni()` / `ToJson()` 同步发布 `unity_version`、`render_pipeline`，INI 统一走 `IniValue()` 换行转义。
- 管线判定：`GraphicsSettings.currentRenderPipeline`（已含 QualitySettings 覆盖）→ `Built-in` / `URP` / `HDRP` / 自定义 SRP 类型全名；与 `pipeline_get` 的 `effective` 同源但归一化为可读类型。异常降级 `unknown` 并告警，不阻断启动。属启动快照，运行中切换管线需重启服务才刷新。
- 客户端无需改动：JS/Python 的 INI 解析忽略未知键，discovery 校验只针对既有字段；`discover` 原样输出 discovery JSON，故两字段亦可从 `/discovery` 读到。
- 自检：`McpDiscoveryTests.TestConnectionInfo` 增 INI/discovery 双写断言、探测一致性断言、含换行值不注入额外 INI 项断言。
- 文档同步：`README.md` / `README.zh-CN.md` / `docs/index.html` / `unity-package/Skills/unity-skilled-mcp/SKILL.md` / 状态窗口安装提示词。`dist/unity-skilled-mcp.zip` 内 SKILL.md 副本按惯例未重打包。
- 验证：`git diff --check` 通过；两个 C# 文件词法括号未闭合 0 / 错配 0。**未在 Unity 内编译/跑自检**（需重载编辑器后跑 `Tools/Unity MCP/Run Discovery Self Tests` 并重启服务查看新 INI）。未 commit/push。

## 本轮：多端 skill.ini 互不覆盖修复（未提交）

- 用户贴出的 INI 只有 `[unity3d-skilled-mcp]` 与 `[project:tdshoweditor]` 两节且逐字段相同；本机 `~/.unity-mcp/skill.ini` 实测一致，只有 1 个在线端（8001 / pid 50316）。**两节相同是设计**：别名节是「最近启动实例」的兼容副本（旧客户端与建模子技能读），新版客户端发现工程节后只读工程节；不是互相覆盖。
- 真实缺陷：`MergeIni` 原先只按 `project_path` 判定旧节，同一工程路径不同进程/端口的端后启动时，会先删掉先启动端的节（`IsProcessAlive` 在其后执行，救不回来）。改为「同路径 + 同 process_id」才算本端旧节；pid 缺失时按 instance_id、再缺失才按旧格式回退；其它在线端节一律保留。节名冲突后缀由 `PathHash(路径)` 改为 `PathHash(路径|进程号)`，同工程多端不撞节名，本端重启节名稳定。
- 客户端消歧：同名多端时 `--project` 会匹配多个，JS/Python 报错补「用 --instance-id 锁定实例」提示。
- 自检：`TestMultiProjectIni` 增同路径双端用例（两节并存、节名不冲突、本端重启不抹掉另一端）。
- 文档同步：`README.md`（顺带把旧的单节示例同步为双节 + `project_name` / `instance_id`）/ `README.zh-CN.md` 两处。
- 验证：`git diff --check` 通过；两 C# 文件词法括号 0 错配；Python 客户端 37/37 通过；JS 客户端 64/67（3 项既有失败：`find` 默认 `kind=all` 与参数校验放宽后测试未同步，HEAD 版本同样失败，与本轮无关）。**未在 Unity 内编译/跑 `Run Discovery Self Tests`**。
- 注意：用户实际运行的是 UPM `PackageCache/io.uframe.unity-skilled-mcp@ac86aa6f48`，源码改动须同步到包后才生效。未 commit/push。

## 本轮：去掉别名节，改由各端 started_at 标识最新（未提交）

- 需求：`skill.ini` 不再写 `[unity3d-skilled-mcp]` 这个「当前实例」组；每个端只在自己的 `[project:<工程名>]` 节里带启动时间，比较即可知谁最新。
- 服务端：`McpService.Start()` 成功后记录 `serviceStartedAtUtc`（改端口重启会刷新），`PublishConnectionInfo()` 通过新增可选参数 `startedAt` 传给 `McpConnectionInfo`；`StartedAt` 以 UTC ISO8601 同时写入 INI 各节与 `/discovery` 的 `started_at`，`updated_at` 仍是本节最后重写时刻。
- `ToIni()` 改为返回本端工程节（默认节名），`MergeIni` 只写本端节；历史别名节仍在清理分支里，旧文件残留于下次重写时删除，`AliasSection` 常量仅用于清理。其它在线端节仍按「同路径 + 同 pid 才算本端旧节」保留。
- 客户端 JS/Python：`ALIAS_SECTION` 只用于读取旧版 INI 兜底（不再要求存在），候选描述带 `started_at`（缺失回退 `updated_at`）；多端仍须 `--project` / `--instance-id` 明确指定，不按时间自动猜测。
- 建模 / UGUI 子技能 `mcp.js`：`readIniSection` → `readLatestIniSection`，读全部 `[project:...]` 节按 `started_at` 取最新（旧文件回退别名节），用于 `skill_dir` 与 `project_path`；导出保留旧名作兼容别名，避免旧调用方 ReferenceError。
- 自检：`TestConnectionInfo` 断言 INI/discovery 双写 `started_at`、只发布工程节；`TestMultiProjectIni` 断言旧别名节被清理、两端各自带不同 `started_at` 且可比出最新。
- 文档同步：`README.md` / `README.zh-CN.md` / `docs/index.html` / 主技能 `SKILL.md` / 两个子技能 `SKILL.md` / 状态窗口安装提示词。
- 验证：`git diff --check` 通过；`McpDiscovery.cs`、`McpDiscoveryTests.cs` 词法括号 0 错配（`McpService.cs` 的括号检查器误报在 HEAD 同样存在，源于插值字符串花括号，非本轮引入）；3 个 JS 与 1 个 Python 脚本语法检查通过；Python 客户端 37/37；JS 客户端 64/67（3 项既有失败与本轮无关）；两份 `mcp.js` 的「取最新端 / 旧别名兜底 / 忽略残留别名节 / 空文件」各 4 组实测通过。**未在 Unity 内编译或跑 `Run Discovery Self Tests`**。
- 破坏性：只认 `[unity3d-skilled-mcp]` 的旧版客户端将读不到新版 INI，须同步包后整体升级。用户实际运行的仍是 UPM `PackageCache/io.uframe.unity-skilled-mcp@ac86aa6f48`，未 commit/push。

