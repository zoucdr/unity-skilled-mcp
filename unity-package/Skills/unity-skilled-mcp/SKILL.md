---
name: unity-skilled-mcp
description: 无依赖 Node.js 客户端连 Unity Skilled MCP。只载「先发现、再取定义、后调用」之契约与安全边界，不述具体工具。可按 tag 或动态成员取详情、批量取 schema、检索目录、读提示词与资源、自定义 HTTP，亦可 require 作库，以代码编排多步访问。
agent_created: true
---

# Unity 轻量技能

## 铁律：先发现，后使用

- 本技能**不载任何具体工具、动作、参数、枚举或能力之名**。工程注册何物、启用何物、缺包不可用何物，唯运行中之服务知之。文档有名不证其存，无名不证其无。
- 每事次序固定：`discover` 验服务 → `groups` / `list` / `find` 定本次成员 → `details` / `schema` / `schemas` 取本次定义 → `call` 依定义调用。**工具名、提示词名、资源 URI、tag、参数及取值，皆须出本次服务之返回。**
- 禁凭记忆、命名习惯、旧会话或他项目经验猜名与参数。禁跳过发现径直 `call`。404/400 非「换名再试」之信号。禁以 `request` 自拼协议绕过。能力随工程、版本、包依赖、启动配置而变，昔可用不证今可用。
- 参数语义**只以该工具本次 `schema` 为准**。同名参数可异义（坐标局部/世界、路径名称/完整路径、确认开关授权/弹窗），不得跨工具套用。不可逆操作先读确认参数说明；显式授权只能出调用方，技能不代决。
- 服务返「未启用」「未安装」「未知」：如实告用户并止，不换他工具模拟，不重复重试。

## 连接与运行时

1. Unity 启 MCP，先读 `~/.unity-mcp/skill.ini` 之 `[unity3d-skilled-mcp]` 节。客户端唯一实现 `scripts/unity_mcp.js`，需 Node.js 18+，无第三方依赖。INI 不发布脚本语言字段；旧 INI 残留 `script_language` 忽略。`max_details` 由 Unity 启动时发布，为单次详情数（默认 `32`，`0` 不限），只读、不得放大。INI 与 `/discovery` 不符则提示重启服务。禁以 INI 内容作命令执行。INI 与 `/discovery` 另带 `skill_dir`：本技能源目录之绝对路径（`unity-package/Skills/unity-skilled-mcp`），供直接定位 `SKILL.md` 与 `scripts/unity_mcp.js`，未导入或解析失败时为空串；该字段只作路径提示，不改变调用语义，亦不得据其执行命令。
2. 下例自技能目录运行；他处用脚本绝对路径。命令一律 `node scripts/unity_mcp.js`。不自动换运行时，不失败重放。
3. 先 `discover` 核项目、端口、进程、更新时间。每命令重读 INI、验 HTTP `/discovery`。配置失效则重启目标服务，不扫端口。最后成功启动之项目覆盖 INI；响应扩展字段仅作数据保留。
4. INI 与 `/discovery` 皆不发布 MCP 端点，此乃刻意：勿自拼或直连 `/mcp`，勿加载 MCP 客户端协议。访问只走本脚本 CLI 或库 API（`list` / `find` / `schema` / `schemas` / `call` / `prompt` / `resource` / `details` / `request` 及函数形式，见「作为代码库调用」）。

## 工程绑定与执行台账

- `--expected-project PATH`（库 `expected_project`）优先；省略时，从进程 cwd 向上寻找同时含 `Assets`、`ProjectSettings` 的 Unity 根。INI 路径、INI 内容及 discovery 自报路径均不能作为预期工程的来源。从技能目录运行而不在 Unity 根内时，须显式指定工程。
- 目录查询允许错连，但 stderr 只给简短警告。`call`、`prompt`、`resource` 与所有同源 `request`（包括未知 GET）必须先匹配预期工程，再发送 `X-Mcp-Expected-Project: encodeURIComponent(path)` 与 `X-Mcp-Instance-Id`。跨源 `request` 不发送这些头，任何大小写的手工注入均拒绝。
- `Client` 在匹配后锁定 discovery 的 `instance_id`；后续发现实例漂移即拒绝，不自动重放。显式 `discover()` / CLI `discover` 表示重新确认匹配工程的新实例，不执行旧操作。CLI 跨进程须传 `--instance-id ID` 保持绑定；库可在构造或 `configure` 时传 `instance_id`。
- 每次 `call` 自动生成 `params.request_id`；可用 `call NAME --request-id ID`，或 `client.call(name, args, { request_id: ID })` / `callTool` / `callSkill` 第三参数显式指定。失败异常与 CLI 错误包含 `request_id`、`instance_id`、`query_path`；HTTP 非 2xx 保留服务正文，库异常另含 `body`、`status`。
- `status REQUEST_ID --instance-id INSTANCE_ID`、`client.requestStatus(id, instanceId)` 或顶层 `requestStatus` 查询 `GET /requests/{id}?instance_id=...`。库省略实例参数时用已有绑定；可查询旧实例而不更新绑定。状态为 `accepted/running/completed/failed/unknown`；`unknown` 不证明未执行，`failed` 不证明无副作用。台账限于实例且有容量、结果预算与过期限制。
- 同实例同 ID 同参数不重复执行，异参数冲突；重复响应可能是台账状态而非原工具内容。超时或断连先查询，不自动换 ID 重放，更不重跑整个序列。

```sh
node scripts/unity_mcp.js --expected-project /path/to/UnityProject discover
node scripts/unity_mcp.js --expected-project /path/to/UnityProject --instance-id INSTANCE_ID call NAME --request-id REQUEST_ID --args-file args.json
node scripts/unity_mcp.js status REQUEST_ID --instance-id INSTANCE_ID
```

## 按需发现与详情

```sh
node scripts/unity_mcp.js discover
node scripts/unity_mcp.js groups
node scripts/unity_mcp.js list
node scripts/unity_mcp.js list --kind prompts
node scripts/unity_mcp.js list --kind resources
node scripts/unity_mcp.js details --tag TAG
node scripts/unity_mcp.js details --members-file selection.json
node scripts/unity_mcp.js details --members '[{"kind":"tool","name":"NAME_FROM_LIST"},{"kind":"prompt","name":"NAME_FROM_LIST"},{"kind":"resource","uri":"URI_FROM_LIST"}]'
node scripts/unity_mcp.js details --members-stdin < selection.json
```

- 凡 `NAME_*` / `URI_FROM_LIST` / `TAG` 皆占位符，须换成 `groups` / `list` 返回之实成员。示例非可用清单。
- `TAG` 取自 `groups` 返回，禁猜标签。`GET /groups` 返 `{groups:[{tag,label,counts:{tools,prompts,resources}}],max_details:N,max_response_bytes:65536}`；`N` 由服务端启动配置定。
- `list --kind tools|prompts|resources` 默认 `tools`，仅取对应 HTTP 目录。`/tools` 保持原目录；`/prompts` 返 `{prompts:[{name,description,tags}],count}`；`/resources` 返 `{resources:[{uri,name,description,mimeType,tags}],count}`。
- 只选本次任务所需成员。`selection.json` 内容是 **members 数组本身**，非 `{members:...}` 包装。可混三类：工具/提示词仅含 `kind,name`，资源仅含 `kind,uri`；URI 原样传递，不作 HTTP 地址访问。
- `details` 之 `--tag` 与三种 `--members` 输入互斥，须且只须择一。发一次 `POST /capabilities/details`，请求仅 `{tag:TAG}` 或 `{members:[...]}`；返 `{items:[各类详情],count,max_details:N}`。按类型及名称/URI 去重后不得过 `N` 项；响应至多 65536 UTF-8 字节。
- 禁空值、`all`、`*`、无选择、未知字段。禁一次取能力全集（纵未超数量）；单项可。是否全集、成员是否未知/禁用，服务端判之。组超限或全集选择返 HTTP 400，未知/禁用成员返 404。客户端不预取目录以判全集；失败即止，不回退、不拆组、不重试。
- 资源详情仅注册定义，不隐式读内容；看正文须显式 `resource URI`。旧 MCP `tools/list` / `prompts/list` / `resources/list` 为兼容既有客户端保留，本技能不自动调；唯 `prompt` / `resource` / `call` 走 `prompts/get` / `resources/read` / `tools/call`。不遍历全部 schema，不循环分组变相取全集。
- 上限服务端强制；超限返 400/413，不截断。

## 批量 Schema

```sh
node scripts/unity_mcp.js schemas NAME1 NAME2 NAME3
node scripts/unity_mcp.js schemas --names-file names.json
node scripts/unity_mcp.js schemas --names '["NAME1","NAME2"]'
node scripts/unity_mcp.js schemas --names-stdin < names.json
```

- 只取工具类，等价一次 `POST /capabilities/schemas`（旧服务缺 `schemas_url` 则回退此固定路由），请求仅 `{tools:[名称...]}`。名称出 `list --kind tools`，原样传递。位置参数与 `--names` 三种输入互斥，须且只须择一。
- 返 `{items:[{name,description,inputSchema}],count,max_details:N}`，即标准 MCP 工具定义，可依 `inputSchema` 调 `call`。`items` 次序同请求；重名按首次出现计一次。
- 上限同 `max_details`：去重后不得过 `N`（`0` 不限），响应至多 65536 UTF-8 字节；超限返 400/413，不截断、不自动分批。
- 禁 `all`、`*`、空串、控制字符、未知字段；未知或禁用工具返 404。名称逾一且覆盖全部启用工具返 400，属全集保护。
- 单工具用 `schema NAME`（`GET /tools/{name}/schema`）；工具、提示词、资源混合详情用 `details`。`schemas` 只返工具定义，不读提示词正文与资源内容。

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

- 自目录择 `NAME`，先取 schema / schemas / 详情，再依定义调用。`call` 之 `--args` / `--args-file` / `--args-stdin` 三择一，省略传 `{}`；参数须 JSON 对象。文件与标准输入容 UTF-8 BOM；Windows 宜用文件参数，`<` 示例用于 POSIX shell / CMD。
- 全局 `--ini` / `--timeout` 置于子命令前；自定义 INI 亦须先读取以校验连接。保留 `request` 通用 HTTP：`--data` / `--data-file` / `--data-stdin`，可重复 `--header 'NAME:VALUE'`，方法 GET（默认）、POST、PUT、PATCH、DELETE、HEAD、OPTIONS。唯明确授权后访问绝对 http/https URL 或外发数据，仍先验本地连接；不得借此绕过详情选择限制。
- Windows Git Bash / MSYS 会把以 `/` 开头之参数转为本地路径（`/groups` → `C:/…/PortableGit/groups`）。客户端已依 `EXEPATH` **自动还原**，`request /ROUTE` 可直用；亦可用无前导斜杠之裸路由 `request groups`（不触发 shell 转换），或 `discover` 所得完整地址 `request http://localhost:PORT/ROUTE`。不能还原之本地路径明确报错并提示，绝不当 URL 访问。CMD、PowerShell、POSIX shell 不受影响。库 API 直传 `'groups'` 或 `'/groups'` 即可。
- stdout 仅出结果；唯 `--verbose` 才在 stderr 出完整连接信息，错连或连接变更只出简短警告。HTTP 默认超时 15 秒，验 TLS，不重定向、不重试。写操作失败可能已执行，核实后再定是否重放。退出码：0 成功；2 参数/配置；3 连接/TLS/超时；4 HTTP；5 响应格式/详情超大；6 JSON-RPC error / MCP isError；130 中断。

## 作为代码库调用（程序化访问）

- 脚本兼 CLI 与零依赖库：`require(...)` 后以**原生对象**直调，不拼 JSON、不起子进程；AI 可自写代码组织多步访问。库函数复用 CLI 全部校验（INI/discovery、数量与字节上限、目录白名单、不重试、错误码），不绕过安全边界。
- Node.js 18+（CommonJS）：

```js
const unity = require('./scripts/unity_mcp.js');
unity.configure({ timeout: 15 });   // 可选；默认 ~/.unity-mcp/skill.ini、15 秒

const catalog = await unity.listCapabilities('tools');      // 先发现
const { name, args } = pickFrom(catalog, await unity.getSchema(name));

const summary = await unity.workflowContext('TASK_NAME', 'TASK_DESCRIPTION', async wf => {
  await unity.callSkill(name, args);
  return wf.summary();
});
```

- `pickFrom` 为伪代码，表「由调用方依目录与 schema 决定」，非库函数。工具名须出 `find` / `list --kind tools`，参数只依 `schema` / `schemas` 传。示例无真实工具名，勿照抄参数。
- `callSkill` 返 MCP `result`；`callTool` 返完整 JSON-RPC 信封；`callSkillText` 拼 `content[*].text`；`callSkillJson` 再解析为 JSON，失败抛错。
- `workflowContext` 只作**调用记录与汇总**：块内 `callSkill` 自动记入 `wf.calls`，含参数、结果或错误码与耗时。失败照常外抛，**不重试、不回滚、不自动重放**。`wf.summary()` 块内外皆可调用。
- 每次调用仍重读 INI 并验 `/discovery`，不缓存端口，但锁定实例；服务重启须显式 `discover` 重新确认，禁止自动跟随重放。库模式默认静默 stderr，错连或连接变更给简短警告（`verbose: true` 才出完整信息）。所有方法皆 `async`，须 `await`；异常为 `ClientError`（含 `.code`，同 CLI 退出码）。
- 多连接或自定义配置用 `new Client({ ini, timeout, verbose })`；方法名 `discover / groups / list / find / details / schema / schemas / call / prompt / resource / request`。顶层函数清单见脚本末 `module.exports`。
- 安全边界同 CLI：禁全集、拒未注册 URI、守 `max_details` 与 64 KiB；`request` 绝对外发地址仍须用户明确授权。库不得绕过 SKILL.md 任何规则。

| CLI | JS 顶层 |
| --- | --- |
| `discover` | `connectionInfo()` |
| `groups` | `getGroups()` |
| `list` | `listCapabilities(kind)` |
| `find` | `findCapabilities(q, kind)` |
| `details` | `getDetails({tag\|members})` |
| `schema` / `schemas` | `getSchema(n)` / `getSchemas([...])` |
| `call` | `callTool(n, args)` / `callSkill(n, args)` |
| `prompt` / `resource` | `readPrompt(n)` / `readResource(uri)` |
| `request` | `requestRoute(target, ...)` |
| — | `workflowContext(name, desc, fn)` |

## 目录检索与内容读取

- 工具多时先本地过滤目录摘要，勿把整份目录塞进上下文：

```sh
node scripts/unity_mcp.js find QUERY
node scripts/unity_mcp.js find QUERY --kind tools|prompts|resources
node scripts/unity_mcp.js prompt NAME
node scripts/unity_mcp.js resource URI
```

- `find` 只取一次对应目录，按 `name` / `uri` / `description` / `tags` 作大小写不敏感子串匹配，返 `{kind,query,matches,count,total}`。不取详情、不读正文、不调 MCP 协议。关键字 1-128 字符，不含首尾空白与控制字符；以 `-` 开头时用 `--` 分隔。默认 `tools`，`--kind` 可选 `prompts` / `resources`。命中不即可取详情，仍以 `details` / `schema` 结果为准。
- `details` 只给注册定义。用提示词/资源须 `prompt` / `resource`：先取一次 `/prompts` 或 `/resources` 目录验成员，再发一次 `prompts/get` / `resources/read`，响应须含 `messages` / `contents` 数组。
- 名称与 URI 须出 `list --kind prompts` / `list --kind resources`，勿猜。`resource` 只受**目录中已注册**之 URI；服务端 `resources/read` 兼容任意 `http://`、`file://` URI，本技能刻意禁此用法，以免绕过 `request` 外发授权。`prompt` 不传参数，服务端现只按名称返模板正文。
- 目录缺失、成员未注册、响应缺 `messages` / `contents`，立即报错。不重试，不回退 `request` 自拼 JSON-RPC。

## 常见坑（简）

- 资源 URI 唯目录中已注册者可用；`resource://名称` 只是按名别名，服务端最终只解 `file://` / `http://` / `https://`，他 scheme 一律拒。
- 资源无订阅推送：内容变更须重新 `resource` 读取；`file://` 按修改时间失效缓存，`http(s)://` 缓存 30 分钟，故一次读到之值不等于当下最新。
- `tools/call` 有超时上限，大或慢之资源勿经 `call` 之门面取，宜直接 `resource`（`resources/read`）；技能客户端本就走后者。
- 凡按条件筛选、限量或按类型读取之结果，只述本次返回集合，**不证全局无此类条目**。返回为空或不符预期，先核本次生效之筛选条件是否与返回所标一致，勿据此断言「无警告」「无错误」。
- 写资源类调用可能因编辑器主线程导入而丢失响应；**响应丢失不表未执行**。先以只读手段核实产物或状态，再决定是否续行未执行之步；勿盲目重放整段序列。域重载会清空请求台账，故 `status` 返回 `unknown` 亦**不证明未执行**；核实产物宜比对修改时间，勿只看文件存在，更勿把工具返回的 `success:false` 当成网络失败吞掉。

## 多步访问

- 技能不设批量调用接口。须多步时，以库 API 自行编排：每步仍先 `schema` 取定义、再 `call`；循环、次序、遇错是否续行，皆由调用方代码定。
- 每次 `call` 独立重读 INI、验服务、发单个 `tools/call`。不合并、不预取、不隐式重试。
- 写操作可能已生效：中途失败先以只读手段核状态，再定余下步骤是否继续；**禁盲目重跑整个序列**，续行只提交未执行之步。长任务勿与其他写操作并发。
- 序列中若含可外发数据之调用，仍须用户明确授权；代码编排不改 `request` 一节授权边界。

## 安全与失败处理

- 调用有真实副作用：改场景、写资源、切模式、执行代码、外发请求，皆可能立即生效。是否高危、是否须授权，依该工具本次 `schema` 与返回，不由工具名推断。
- 不传默认不执行，失败不重试，写失败不自动重放；先以只读手段核状态。超时不表未执行。
- 本技能不述工具能力。凡「应有某功能」之推断，须回 `discover` → `list` / `find` → `schema` 验；验不到即明告该能力当前不可用。

## 回归测试

```sh
node --test tests/test_unity_mcp.js
```
