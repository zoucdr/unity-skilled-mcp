---
name: unity3d-skill
description: 通过无依赖 JS 或 Python 客户端连接 Unity MCP 服务，按固定 tag 或本次动态成员集合读取工具、提示词和资源定义详情，支持一次获取多个工具的完整 Schema、目录关键字检索、提示词与资源正文读取、顺序批量工具调用，保留目录、单工具 schema/call 与自定义 HTTP；覆盖引擎构建与设置、动画状态机、UI Toolkit、相机与 Cinemachine、线渲染与 VFX、Test Runner 与 ProBuilder。
agent_created: true
---

# Unity 轻量技能

## 连接与解释器
1. 在 Unity 启动 MCP，先读取 `~/.unity-mcp/skill.ini` 的 `[unity3d-skilled-mcp]` 节。按 `script_language` 选择解释器：`js` → Node.js 18+，`python` → Python 3.8+；缺字段默认 `js`，去掉两端空白后空值或其他值须停止并提示修正。`max_details` 是 Unity 启动时发布的单次详情数量（默认 `32`，`0` 表示不限数量），客户端只读取、不自行放大；INI 与服务 `/discovery` 不一致时提示重启服务。禁止将 INI 内容当作命令执行。
2. 从技能目录运行以下示例；其他目录使用脚本绝对路径。若选 `python`，将 `node scripts/unity_mcp.js` 替换为 `python scripts/unity_mcp.py`（POSIX 可用 `python3`）。双语言命令一致、无外部依赖，不自动切换运行时或失败重放。
3. 先 `discover` 核对项目、端口、进程和更新时间。每次命令重读 INI 并验证 HTTP `/discovery`；配置失效时重启目标服务，不扫描端口。最后成功启动的项目覆盖 INI；响应扩展字段仅作数据保留。
4. INI 与 `/discovery` 都不发布 MCP 端点地址，这是刻意设计：不要自行拼接或直连 `/mcp`、不要加载 MCP 客户端协议，所有访问都走本技能脚本的 `list` / `find` / `schema` / `schemas` / `call` / `calls` / `prompt` / `resource` / `details` / `request`。

## 按需发现与详情
```sh
node scripts/unity_mcp.js discover
node scripts/unity_mcp.js groups
node scripts/unity_mcp.js list
node scripts/unity_mcp.js list --kind prompts
node scripts/unity_mcp.js list --kind resources
node scripts/unity_mcp.js details --tag TAG
node scripts/unity_mcp.js details --members-file selection.json
node scripts/unity_mcp.js details --members '[{"kind":"tool","name":"hierarchy_search"},{"kind":"prompt","name":"foo"},{"kind":"resource","uri":"res://foo"}]'
node scripts/unity_mcp.js details --members-stdin < selection.json
node scripts/unity_mcp.js schemas NAME1 NAME2 NAME3
node scripts/unity_mcp.js schemas --names-file names.json
node scripts/unity_mcp.js schemas --names '["a","b"]'
node scripts/unity_mcp.js schemas --names-stdin < names.json
```
- 从 `groups` 返回值选择实际 `TAG`，禁止猜标签。`GET /groups` 返回 `{groups:[{tag,label,counts:{tools,prompts,resources}}],max_details:N,max_response_bytes:65536}`，`N` 由服务端启动配置决定。
- `list --kind tools|prompts|resources` 默认 `tools`，仅取相应 HTTP 目录。`/tools` 保持原目录；`/prompts` 返回 `{prompts:[{name,description,tags}],count}`；`/resources` 返回 `{resources:[{uri,name,description,mimeType,tags}],count}`。
- 仅针对本次任务选成员；示例名称须替换为目录中实际启用项。`selection.json` 内容是 **members 数组本身**，不是 `{members:...}` 包装对象。允许混合三类；工具/提示词仅含 `kind,name`，资源仅含 `kind,uri`，URI 原样传递、不作为 HTTP 地址访问。
- `details` 的 `--tag` 与三种 `--members` 输入互斥，必须且只能选一种。发送一次 `POST /capabilities/details`，请求仅为 `{tag:TAG}` 或 `{members:[...]}`；返回 `{items:[各类详情],count,max_details:N}`。按类型及名称/URI 去重后不得超过 `N` 项，响应最多 65536 UTF-8 字节。
- 禁止空值、`all`、`*`、无选择及未知字段。禁止一次取得整个能力全集（即使未超过数量限制）；单项允许。是否为全集、成员是否未知/禁用由服务端判定；组超限或全集选择返回 HTTP 400，未知/禁用成员返回 404。客户端不为判断全集预取所有目录；失败不回退、不拆组遍历或重试。
- 资源详情仅为注册定义，不隐式读取内容；要看正文必须显式用 `resource URI`。本技能不自动调用旧 MCP `tools/list`、`prompts/list`、`resources/list`（这些接口为兼容既有 Unity MCP 客户端仍保持原行为）；只有 `prompt` / `resource` / `call` / `calls` 这类显式命令才会走 `prompts/get`、`resources/read`、`tools/call`。不自动遍历全部 schema，也不循环分组变相拉取全集。
- 数量上限与字节上限由服务端强制；超限返回 400/413，不静默截断。

## 批量 Schema
```sh
node scripts/unity_mcp.js schemas hierarchy_search hierarchy_apply
node scripts/unity_mcp.js schemas --names-file names.json
```
- 只取工具类，等价于一次 `POST /capabilities/schemas`（缺 `schemas_url` 的旧服务回退该固定路由），请求仅为 `{tools:[名称...]}`。名称来自 `list --kind tools`，原样传递；`schemas` 的位置参数与 `--names` 三种输入互斥，必须且只能选一种。
- 返回 `{items:[{name,description,inputSchema}],count,max_details:N}`，即标准 MCP 工具定义，可直接按 `inputSchema` 调用 `call`。`items` 顺序与请求一致，重复名称按首次出现计一次。
- 上限复用 `max_details`：去重后不得超过 `N`（`0` 表示不限数量），响应最多 65536 UTF-8 字节；超量或超字节返回 400/413，不静默截断、不自动分批。
- 禁止 `all`、`*`、空串、控制字符和未知字段；未知或已禁用工具返回 404。名称超过 1 个且覆盖全部已启用工具时返回 400，属于全集保护。
- 单工具仍用 `schema NAME`（`GET /tools/{name}/schema`）；需要工具、提示词、资源混合详情时用 `details`。`schemas` 只返回工具定义，不读取提示词正文和资源内容。

## 单工具与 HTTP
```sh
node scripts/unity_mcp.js schema NAME
node scripts/unity_mcp.js schemas NAME1 NAME2
node scripts/unity_mcp.js call NAME --args-file args.json
node scripts/unity_mcp.js call NAME --args '{}'
node scripts/unity_mcp.js call NAME --args-stdin < args.json
node scripts/unity_mcp.js request /CUSTOM_ROUTE --method POST --data-file body.json
node scripts/unity_mcp.js --ini /path/to/config.ini --timeout 30 groups
node scripts/unity_mcp.js details --help
```
从目录选择 `NAME`，先读单个 schema、批量 schemas 或所选详情，再按 schema 调用。`call` 的 `--args` / `--args-file` / `--args-stdin` 三选一，省略传 `{}`，参数必须为 JSON 对象。文件及标准输入均支持 UTF-8 BOM；Windows 推荐文件参数，`<` 示例用于 POSIX shell / CMD。

全局 `--ini` / `--timeout` 放在子命令前；自定义 INI 也须先读取以选择解释器。保留 `request` 通用 HTTP：`--data` / `--data-file` / `--data-stdin`，可重复 `--header 'NAME:VALUE'`，方法 GET（默认）、POST、PUT、PATCH、DELETE、HEAD、OPTIONS。仅在明确授权后访问绝对 http/https URL 或外发数据，仍先验证本地连接；不可借此绕过详情选择限制。

stdout 输出结果，stderr 输出连接信息。默认每次 HTTP 超时 15 秒，验证 TLS，不重定向、不重试。写操作失败可能已执行，核实后再决定是否重放。退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式/详情超大；6 JSON-RPC error / MCP isError；130 中断。`calls` 是唯一例外：中途失败也会输出已执行步骤（`exit_code` 字段即退出码），便于判断哪些写操作已经生效。

## 目录检索与内容读取

工具较多时先在本地过滤目录摘要，避免把整份目录塞进上下文：

```sh
node scripts/unity_mcp.js find QUERY
node scripts/unity_mcp.js find QUERY --kind tools|prompts|resources
node scripts/unity_mcp.js prompt NAME
node scripts/unity_mcp.js resource URI
```

- `find` 只取一次对应目录，按 `name` / `uri` / `description` / `tags` 做大小写不敏感的子串匹配，返回 `{kind,query,matches,count,total}`；不取详情、不读正文、不调用 MCP 协议。关键字为 1-128 字符、不含首尾空白与控制字符；以 `-` 开头时用 `--` 分隔。默认 `tools`，`--kind` 可选 `prompts` / `resources`。
- `details` 只给注册定义；要真正使用提示词或资源，用 `prompt` / `resource`：先各取一次 `/prompts` 或 `/resources` 目录校验成员存在，再发一次 `prompts/get` 或 `resources/read`，分别要求响应含 `messages` / `contents` 数组。
- 名称与 URI 必须来自 `list --kind prompts` / `list --kind resources`，不要猜。`resource` 只接受**目录中已注册**的 URI；服务端的 `resources/read` 兼容任意 `http://`、`file://` URI，本技能刻意禁止这种用法，避免绕过 `request` 的外发授权。`prompt` 不传参数，服务端当前只按名称返回模板正文。
- 目录缺失、成员未注册或响应缺少 `messages` / `contents` 时立即报错，不重试、不回退到 `request` 自行拼接 JSON-RPC。

## 批量工具调用

```sh
node scripts/unity_mcp.js calls --calls '[{"name":"hierarchy_search","arguments":{"query":"Player"}},{"name":"edit_gameobject","arguments":{"action":"get_components"}}]'
node scripts/unity_mcp.js calls --calls-file steps.json
node scripts/unity_mcp.js calls --calls-stdin < steps.json
```

- 输入是 **数组本身**：元素仅含 `name` 与可选 `arguments`（必须是 JSON 对象，省略为 `{}`），名称来自 `list --kind tools`，按 schema 传参；调用前仍应先取所需 `schemas`。
- `--calls` / `--calls-file` / `--calls-stdin` 三选一且必须选一种；文件与标准输入支持 UTF-8 BOM。数量上限复用 `max_details`（默认 `32`，`0` 不限数量），整体请求体最多 65536 UTF-8 字节；超限在发送任何调用前报错，不自动分批。
- 按数组顺序执行，每次一个 `tools/call`、JSON-RPC `id` 依次递增；**任一步失败即停**，后续调用不再发送。输出 `{steps:[{index,name,ok,result|error}],executed,total,stopped_at,exit_code}`：全部成功时 `stopped_at=null`、`exit_code=0`；失败时保留已成功步骤，`exit_code` 为该步错误码。
- 批量调用不提供事务或回滚。失败后先用只读工具核实状态（例如 `get_info`、`get_build_status`、`console_read`）再决定是否重放，**禁止盲目重跑整个数组**；重放时只提交尚未执行的步骤。构建、切平台、重编译、测试运行期间不要与其他写操作并发。
- 数组里若包含 `request_http` 之类可外发数据的工具，仍须用户明确授权；`calls` 不改变 `request` 一节的授权边界。

## 引擎操作

引擎级操作统一走 `engine_control`。先取一次定义（`schema engine_control` 或 `schemas engine_control`），再按 `action` 调用：

```sh
node scripts/unity_mcp.js call engine_control --args '{"action":"get_info"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"get_selection"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"set_active_tool","tool":"move"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"get_build_settings"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"set_build_scenes","mode":"add","scenes":["Assets/Scenes/Main.unity"]}'
node scripts/unity_mcp.js call engine_control --args '{"action":"build_player","output_path":"Builds/Windows/Game.exe","development":true}'
node scripts/unity_mcp.js call engine_control --args '{"action":"get_build_status"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"get_project_settings"}'
node scripts/unity_mcp.js call engine_control --args '{"action":"get_compilation"}'
```

- 上下文：`get_info` 返回 Unity 版本、平台、活动构建目标、播放/编译状态、当前编辑器工具、活动场景、焦点窗口。开始引擎任务前先读一次；需要对照选择集时用 `get_selection`。
- 选择集与工具：`set_selection` 接受 `instance_ids` 或 `paths`（场景对象路径或资源路径），`clear` 为 true 时先清空；无匹配项不会报错，会在 `unresolved` 中返回。`set_active_tool` 取值 `none`、`move`、`rotate`、`scale`、`rect`、`transform`、`custom`、`view`。
- 构建流程：`get_build_settings` 查看场景列表、启用数量与开发标记 → `set_build_scenes`（`mode` 为 `replace`、`add`、`remove`、`enable`、`disable`、`clear`，路径必须是已存在的 `.unity` 资源）→ 需要时 `set_build_target` 切换平台（会触发资源重新导入）→ `build_player`。
- 构建执行：`build_player` 需要 `output_path`（绝对路径或相对工程根目录）；`target` 省略时用当前活动平台，`scenes` 省略时用已启用场景，`development`、`auto_run` 可选。默认排队后立即返回 `job_id`，用 `get_build_status` 轮询 `state`（`queued`/`running`/`succeeded`/`failed`）；`wait=true` 同步等待并直接返回最终状态。
- 工程设置：`get_project_settings` 读取 Player、时间、质量、物理、序列化与进入播放模式选项；`set_project_settings` 只应用传入字段（`company_name`、`product_name`、`bundle_version`、`bundle_version_code`、`run_in_background`、`color_space`、`fixed_delta_time`、`time_scale`、`quality_level`、`gravity`、`enter_play_mode_options`、`serialization_mode`）。任一字段非法时整批不应用并返回 `errors`。
- 编译：`get_compilation` 返回 `is_compiling`、`script_compilation_failed`、`diagnostics_available`、最近最多 100 条错误/警告及 `truncated`；`diagnostics_available=false` 表示当前 Unity 版本未提供编译诊断事件，此时空 `messages` 不代表编译无错误，应以 `script_compilation_failed` 与 Console 为准。`recompile` 请求脚本编译，随后轮询 `get_compilation`。
- 其他：`undo` / `redo` 执行撤销重做；`step` 仅在播放（含暂停）时前进一帧。
- 分工：播放/暂停/停止、输入模拟与截图用 `gameplay`；窗口、菜单、编辑器整体状态用 `base_editor`；场景资源的创建/加载/保存用 `edit_scene`；包依赖用 `manage_package`。`engine_control` 只负责构建、平台、工程设置、编译与编辑器工具/选择集，避免重复调用。
- 约束：播放模式中不能构建或切换平台；构建、平台切换、重编译都会阻塞或占用编辑器主线程，等待期间不要并发发起其他写操作；失败后先用 `get_build_status` 或 `get_compilation` 核实状态，再决定是否重试，禁止盲目重放构建。

## 动画状态机（edit_animator）

`edit_animator` 管 Animator Controller 的结构与运行时驱动；动画片段本身（曲线、事件）仍用 `edit_anim_clip`。

```sh
node scripts/unity_mcp.js call edit_animator --args '{"action":"create","path":"Assets/Anim/Player.controller"}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"add_parameter","path":"Assets/Anim/Player.controller","parameter":"Speed","parameter_type":"float"}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"add_state","path":"Assets/Anim/Player.controller","state":"Idle","motion":"Assets/Anim/Idle.anim"}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"set_default_state","path":"Assets/Anim/Player.controller","state":"Idle"}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"add_transition","path":"Assets/Anim/Player.controller","state":"Idle","target_state":"Run","conditions":["Speed:Greater:0.1"]}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"add_blend_tree","path":"Assets/Anim/Player.controller","state":"Locomotion","blend_parameter":"Speed"}'
node scripts/unity_mcp.js call edit_animator --args '{"action":"play","target":"Player","state":"Run"}'
```

- 结构类动作（`create`、`add_*`、`set_*`、`remove_*`）需要控制器资源 `path`；`layer` 可写层名或从 0 开始的索引，省略为第一层。
- 过渡条件统一写 `NAME:MODE:THRESHOLD`，`MODE` 取 `If`、`IfNot`、`Greater`、`Less`、`Equals`、`NotEqual`；参数必须已在控制器中定义，否则该条件被跳过并在返回的 `conditions` 中说明。
- 混合树目前支持 1D（`blend_parameter` + `add_blend_tree_child` 的 `threshold`）。
- 运行时动作（`play`、`crossfade`、`set_parameter`、`get_runtime`）要求 Play 模式，并用 `target` 指向带 Animator 的对象；编译期不需要 Animator 组件存在。
- 控制器结构改动会保存资源；不要在播放中改结构，先 `gameplay` 停止播放。

## UI Toolkit（ui_toolkit）

UI Toolkit 与 UGUI 分开：`ui_toolkit` 处理 UXML/USS/UIDocument/PanelSettings，`ugui_layout` 仍负责 Canvas 与 RectTransform 布局。

```sh
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"create_uxml","path":"Assets/UI/Main.uxml","name":"root"}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"add_element","path":"Assets/UI/Main.uxml","parent":"root","name":"title","element_type":"Label"}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"set_element","path":"Assets/UI/Main.uxml","name":"title","attribute":"text","value":"Main Menu"}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"append_uss","path":"Assets/UI/Main.uss","content":".title { font-size: 28px; }"}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"create_panel_settings","path":"Assets/UI/PanelSettings.asset","scale_mode":"ScaleWithScreenSize","reference_dpi":96}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"attach_document","game_object":"Canvas/UI","uxml":"Assets/UI/Main.uxml","panel_settings":"Assets/UI/PanelSettings.asset"}'
node scripts/unity_mcp.js call ui_toolkit --args '{"action":"get_document","game_object":"Canvas/UI"}'
```

- UXML/USS 以文本读写，写入前会校验 XML；元素定位依赖 `name` 属性，先 `list_elements` 确认实际名称，不要猜。
- `set_element` 默认把 `style` 追加到现有样式后（用分号分隔），`replace=true` 才整体替换；`attribute` 也可写 `text`、`class` 或自定义属性。
- `attach_document` 会添加或复用 `UIDocument` 组件并赋 `visualTreeAsset`、`panelSettings`；`detach_document` 移除组件。两者都会标脏当前场景。
- `screenshot` 抓 Game View 预览（需要编辑器窗口可见），不是离屏渲染。

## 相机（manage_camera）

```sh
node scripts/unity_mcp.js call manage_camera --args '{"action":"create","name":"Cutscene Cam","position":[0,3,-8],"look_at":[0,1,0],"fov":45}'
node scripts/unity_mcp.js call manage_camera --args '{"action":"set","name":"Cutscene Cam","fov":60,"background":"#101010FF"}'
node scripts/unity_mcp.js call manage_camera --args '{"action":"look_at","name":"Cutscene Cam","target":"Player"}'
node scripts/unity_mcp.js call manage_camera --args '{"action":"screenshot","name":"Cutscene Cam","save_path":"Assets/Screenshots/cutscene.png","width":1920,"height":1080}'
node scripts/unity_mcp.js call manage_camera --args '{"action":"screenshot_multiview","folder":"Assets/Screenshots"}'
node scripts/unity_mcp.js call manage_camera --args '{"action":"cm_status"}'
```

- `screenshot` 从指定相机离屏渲染，与 `gameplay`（Game View）和 `scene_view`（Scene View）互补；省略 `name` 时用 `Camera.main`。
- `screenshot_multiview` 默认只抓启用且激活的相机，最多 32 个；可用 `cameras` 指定名单或 `all_cameras=true` 放宽。
- Cinemachine 动作需要 `com.unity.cinemachine`。先调用 `cm_status` 确认已安装并取得虚拟相机列表，再用 `cm_create` / `cm_set`（`priority`、`fov`、`follow`、`target`、`enabled`）/ `cm_brain`。未安装时返回明确错误，不要反复重试。

## 特效（edit_effects）

`edit_effects` 覆盖 LineRenderer、TrailRenderer 与 VFX Graph；传统 Particle System 继续用 `edit_particle_system`。

```sh
node scripts/unity_mcp.js call edit_effects --args '{"action":"create_line","name":"Laser","points":[[0,0,0],[0,0,10]],"color":"#FF4040FF","width":0.05}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"set_line_points","target":"Laser","mode":"add","points":[[0,0,20]]}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"create_trail","name":"Dash Trail","time":0.6,"width":0.2}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"vfx_list"}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"vfx_set","target":"FX/Spark","parameter":"SpawnRate","value_type":"float","value":120}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"vfx_event","target":"FX/Spark","event_name":"OnBurst"}'
node scripts/unity_mcp.js call edit_effects --args '{"action":"vfx_control","target":"FX/Spark","control":"play"}'
```

- 点位数组是 `[[x,y,z], ...]`；`set_line_points` 的 `mode` 为 `replace`、`add`、`clear`。
- 未提供 `material` 时，若渲染器没有材质会自动挂一个默认 shader 的材质，正式资源请显式传材质路径。
- `vfx_*` 需要 `com.unity.visualeffectgraph`；`vfx_set` 的取值由 `value_type`（`float`、`int`、`bool`、`vector3`、`color`）决定，参数名必须是 VFX 资源里暴露的属性名。

## 测试（run_tests）

```sh
node scripts/unity_mcp.js call run_tests --args '{"action":"run","mode":"EditMode"}'
node scripts/unity_mcp.js call run_tests --args '{"action":"run","mode":"PlayMode","category_names":["Fast"],"assembly_names":["Game.Tests"]}'
node scripts/unity_mcp.js call run_tests --args '{"action":"get_status"}'
```

- 需要 `com.unity.test-framework`。`run` 立即返回 `job_id` 和 `state=running`，之后轮询 `get_status` 取 `state`（`running`/`finished`/`error`）、`passed`/`failed`/`skipped`/`inconclusive`、`total`、`duration_seconds` 与 `failed_tests`。
- 同时只允许一个测试任务；重复 `run` 返回 `tests_running`，先轮询或用 `clear` 复位卡住的状态。
- PlayMode 测试会占用编辑器并切换播放状态；运行期间不要并发发起其他写操作。

## 建模（edit_probuilder）

```sh
node scripts/unity_mcp.js call edit_probuilder --args '{"action":"status"}'
node scripts/unity_mcp.js call edit_probuilder --args '{"action":"create_shape","name":"PBCube","shape":"Cube","position":[0,0.5,0]}'
node scripts/unity_mcp.js call edit_probuilder --args '{"action":"get_mesh_info","target":"PBCube"}'
node scripts/unity_mcp.js call edit_probuilder --args '{"action":"extrude_faces","target":"PBCube","extrude_method":"FaceNormal","distance":0.5,"face_indices":[0]}'
```

- 需要 `com.unity.probuilder`。先 `status` 判断是否安装，未安装时直接告知用户，不要用其他工具模拟。
- `face_indices` 省略时作用于全部面；`extrude_faces` 要求 `distance` 非 0。

## 可选包与通用做法

- Cinemachine、VFX Graph、ProBuilder、Test Framework 都是**可选包**，本包用运行时反射访问，未安装只会让对应工具返回「未安装」错误，不影响其他工具。
- 遇到可选包相关失败：先跑该工具的探测动作（`edit_probuilder status`、`manage_camera cm_status`、`edit_effects vfx_list`、`run_tests get_status`），确认是「未安装」还是「参数/对象不对」，不要把未安装当成 bug 反复重试。
- 所有新建对象都会标脏当前场景；需要持久化时用 `edit_scene` 保存场景或 `project_operate` 保存资源。

## 回归测试
```sh
node --test tests/test_unity_mcp.js
python -B -m unittest discover -s tests -p 'test_*.py'
```