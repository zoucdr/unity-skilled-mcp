---
name: unity3d-skill
description: 通过无依赖 JS 或 Python 客户端连接 Unity MCP 服务，按固定 tag 或本次动态成员集合读取工具、提示词和资源定义详情，保留目录、单工具 schema/call 与自定义 HTTP；用于操作 Unity 项目。
agent_created: true
---

# Unity 轻量技能

## 连接与解释器
1. 在 Unity 启动 MCP，先读取 `~/.unity-mcp/skill.ini` 的 `[unity3d-skilled-mcp]` 节。按 `script_language` 选择解释器：`js` → Node.js 18+，`python` → Python 3.8+；缺字段默认 `js`，去掉两端空白后空值或其他值须停止并提示修正。`max_details` 是 Unity 启动时发布的单次详情数量（默认 `32`，`0` 表示不限数量），客户端只读取、不自行放大；INI 与服务 `/discovery` 不一致时提示重启服务。禁止将 INI 内容当作命令执行。
2. 从技能目录运行以下示例；其他目录使用脚本绝对路径。若选 `python`，将 `node scripts/unity_mcp.js` 替换为 `python scripts/unity_mcp.py`（POSIX 可用 `python3`）。双语言命令一致、无外部依赖，不自动切换运行时或失败重放。
3. 先 `discover` 核对项目、端口、进程和更新时间。每次命令重读 INI 并验证 HTTP `/discovery`；配置失效时重启目标服务，不扫描端口。最后成功启动的项目覆盖 INI；响应扩展字段仅作数据保留。
4. INI 与 `/discovery` 都不发布 MCP 端点地址，这是刻意设计：不要自行拼接或直连 `/mcp`、不要加载 MCP 客户端协议，所有访问都走本技能脚本的 `list` / `schema` / `call` / `details` / `request`。

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
```
- 从 `groups` 返回值选择实际 `TAG`，禁止猜标签。`GET /groups` 返回 `{groups:[{tag,label,counts:{tools,prompts,resources}}],max_details:N,max_response_bytes:65536}`，`N` 由服务端启动配置决定。
- `list --kind tools|prompts|resources` 默认 `tools`，仅取相应 HTTP 目录。`/tools` 保持原目录；`/prompts` 返回 `{prompts:[{name,description,tags}],count}`；`/resources` 返回 `{resources:[{uri,name,description,mimeType,tags}],count}`。
- 仅针对本次任务选成员；示例名称须替换为目录中实际启用项。`selection.json` 内容是 **members 数组本身**，不是 `{members:...}` 包装对象。允许混合三类；工具/提示词仅含 `kind,name`，资源仅含 `kind,uri`，URI 原样传递、不作为 HTTP 地址访问。
- `details` 的 `--tag` 与三种 `--members` 输入互斥，必须且只能选一种。发送一次 `POST /capabilities/details`，请求仅为 `{tag:TAG}` 或 `{members:[...]}`；返回 `{items:[各类详情],count,max_details:N}`。按类型及名称/URI 去重后不得超过 `N` 项，响应最多 65536 UTF-8 字节。
- 禁止空值、`all`、`*`、无选择及未知字段。禁止一次取得整个能力全集（即使未超过数量限制）；单项允许。是否为全集、成员是否未知/禁用由服务端判定；组超限或全集选择返回 HTTP 400，未知/禁用成员返回 404。客户端不为判断全集预取所有目录；失败不回退、不拆组遍历或重试。
- 资源详情仅为注册定义，不隐式读取内容。本技能不自动调用旧 MCP `tools/list`、`prompts/list`、`resources/list`（这些接口为兼容既有 Unity MCP 客户端仍保持原行为），不自动遍历全部 schema，也不循环分组变相拉取全集。
- 数量上限与字节上限由服务端强制；超限返回 400/413，不静默截断。

## 单工具与 HTTP
```sh
node scripts/unity_mcp.js schema NAME
node scripts/unity_mcp.js call NAME --args-file args.json
node scripts/unity_mcp.js call NAME --args '{}'
node scripts/unity_mcp.js call NAME --args-stdin < args.json
node scripts/unity_mcp.js request /CUSTOM_ROUTE --method POST --data-file body.json
node scripts/unity_mcp.js --ini /path/to/config.ini --timeout 30 groups
node scripts/unity_mcp.js details --help
```
从目录选择 `NAME`，先读单个 schema 或所选详情，再按 schema 调用。`call` 的 `--args` / `--args-file` / `--args-stdin` 三选一，省略传 `{}`，参数必须为 JSON 对象。文件及标准输入均支持 UTF-8 BOM；Windows 推荐文件参数，`<` 示例用于 POSIX shell / CMD。

全局 `--ini` / `--timeout` 放在子命令前；自定义 INI 也须先读取以选择解释器。保留 `request` 通用 HTTP：`--data` / `--data-file` / `--data-stdin`，可重复 `--header 'NAME:VALUE'`，方法 GET（默认）、POST、PUT、PATCH、DELETE、HEAD、OPTIONS。仅在明确授权后访问绝对 http/https URL 或外发数据，仍先验证本地连接；不可借此绕过详情选择限制。

stdout 输出结果，stderr 输出连接信息。默认每次 HTTP 超时 15 秒，验证 TLS，不重定向、不重试。写操作失败可能已执行，核实后再决定是否重放。退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式/详情超大；6 JSON-RPC error / MCP isError；130 中断。

## 回归测试
```sh
node --test tests/test_unity_mcp.js
python -B -m unittest discover -s tests -p 'test_*.py'
```