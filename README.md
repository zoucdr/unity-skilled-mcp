[English](README.md) | [中文](README.zh-CN.md)

---

# Unity Skilled MCP

**A lightweight Skill + MCP workflow for controlling the Unity Editor with AI.**

Unity Skilled MCP runs its service inside the Unity Editor and exposes a large set of Unity development capabilities. The lightweight Skill keeps the agent's initial context small: it discovers short capability summaries first, loads only the relevant definitions when needed, and delegates execution to the Unity service.

![Unity Skilled MCP](docs/unity3d-mcp.png)

## Why Skill + MCP?

- **Progressive discovery:** start with capability names, short descriptions, and tags; retrieve only the schemas you actually need, one at a time or several per request.
- **Tools, prompts, and resources:** browse each catalog, retrieve a tagged group, or request a task-specific mixed set.
- **Automatic local connection discovery:** Unity writes the active service information to `~/.unity-mcp/skill.ini` whenever the service starts successfully.
- **Configurable detail limits:** the per-request item limit is configurable in Unity; the response is also capped at 64 KiB, and full-catalog detail requests are rejected.
- **Single runtime:** one dependency-free Node.js 18+ client (`scripts/unity_mcp.js`); no language selection in Unity or in the INI.
- **Legacy MCP compatibility:** existing clients can continue using the original MCP list, call, prompt, and resource operations.
- **Engine-level control:** build the player, switch platforms, manage build scenes, read or change engine settings, inspect compilation, and control the editor selection and active tool.
- **Unity-safe execution:** editor operations are dispatched through the Unity Editor's main-thread execution flow.

## Architecture

```text
AI agent
  └─ Unity Skill (SKILL.md + unity_mcp.js)
       ├─ Reads ~/.unity-mcp/skill.ini and validates the active service
       ├─ Discovers short summaries and selected capability definitions over HTTP
       └─ Invokes the requested operation through the client script
            └─ Unity Editor service → main-thread execution → Unity APIs

Existing MCP clients ────────────────────────────────┘
```

The Skill is the recommended path for agents that support local Skills. Do not have an agent bypass the Skill and construct MCP requests itself. The INI intentionally does **not** contain an MCP endpoint field.

Tools are registered declaratively: one action equals one tool, named `prefix.action`, with an exact `inputSchema`. There is no state tree — `StateTree`, `StateTreeBuilder`, `StateTreeContext`, `StateMethodBase`, `DualStateMethodBase` and the selector system have all been removed, and tool classes are plain stateless classes marked with `[ToolModule]` / `[ToolAction]`.

## Quick start

### 1. Install the Unity package

Import the `unity-package` folder into a Unity project using your normal package workflow.

### 2. Start the service

In Unity, open **Window → MCP → Status**, review the service settings, and start the server. The default port is `8000`. On every successful start, Unity writes the current connection and Skill runtime settings to:

```text
~/.unity-mcp/skill.ini
```

Changing the port takes effect when the service is restarted. The most recently started Unity project publishes the active connection information to this file.

### 3. Install the Skill

Recommended: in the MCP Status window, click **Copy skill install prompt** and paste the prompt into your AI agent. It includes the Skill installation instructions and the Skill folder's resolved disk path.

You can also install it manually:

- Copy `unity-package/Skills/unity-skilled-mcp/` into the agent's Skills directory, keeping the folder name `unity-skilled-mcp`.
- Or use the packaged archive at `dist/unity-skilled-mcp.zip`.

### 4. Use it from the agent

Ask the agent to use the Unity Skill, then describe the Unity task in natural language. The Skill guides the agent to verify the connection, discover the relevant capability, read its definition, and invoke it.

## Unity settings and connection file

The MCP Status window exposes the maximum number of capability details returned by one request. The setting is published on the next successful service start.

| Setting | Values | Default / behavior |
| --- | --- | --- |
| Service port | Valid port number | `8000` |
| Maximum details per request | Non-negative integer | `32`; `0` removes the item-count limit |

The response-size limit remains **65,536 UTF-8 bytes** even when the item-count limit is `0`. A request that selects every available capability is rejected. The service does not silently truncate an oversized result.

Example `~/.unity-mcp/skill.ini`:

```ini
[unity3d-skilled-mcp]
base_url=http://localhost:8000
port=8000
project_path=/path/to/UnityProject
process_id=12345
updated_at=2026-10-30T02:00:00.0000000Z
max_details=32
```

The file contains local connection metadata and startup settings, but **does not publish `mcp_url`** and no longer publishes a script language. The Skill client reads the file dynamically on each command and validates the service before proceeding; it does not scan ports or automatically replay failed write operations.

## How progressive discovery works

1. **Verify the service:** read the INI and check the current service's discovery metadata.
2. **Browse summaries:** read groups or a specific tools, prompts, or resources catalog.
3. **Load only what is needed:** read one tool schema, fetch several tool schemas in one request, request a fixed tag group, or submit a task-specific list of members. `find QUERY` filters catalog summaries locally when the tool list grows.
4. **Execute:** call the selected operation or read a listed prompt/resource body. Multi-step work is scripted by the caller through the JS library API; the Skill ships no batch-call command.

Typical commands, run from the Skill folder:

```sh
# Confirm project and service metadata
node scripts/unity_mcp.js discover

# Browse groups and short catalogs
node scripts/unity_mcp.js groups
node scripts/unity_mcp.js list --kind tools
node scripts/unity_mcp.js list --kind prompts
node scripts/unity_mcp.js list --kind resources

# Load one group or a curated mixed set
node scripts/unity_mcp.js details --tag hierarchy-management
node scripts/unity_mcp.js details --members-file selection.json

# Load a single tool definition, or several at once, then call it
node scripts/unity_mcp.js schema hierarchy_search
node scripts/unity_mcp.js schemas hierarchy_search hierarchy_apply
node scripts/unity_mcp.js schemas --names-file names.json
node scripts/unity_mcp.js call hierarchy_search --args-file args.json

# Filter catalogs locally, or use a listed prompt/resource
node scripts/unity_mcp.js find camera --kind tools
node scripts/unity_mcp.js prompt NAME
node scripts/unity_mcp.js resource URI
```

The tag in `details --tag` must come from the live group catalog. A `selection.json` file contains the members array itself, for example:

```json
[
  { "kind": "tool", "name": "hierarchy_search" },
  { "kind": "prompt", "name": "replace-with-a-listed-prompt" },
  { "kind": "resource", "uri": "replace-with-a-listed-resource-uri" }
]
```

The client requires Node.js 18+ and no third-party packages. Consult [`SKILL.md`](unity-package/Skills/unity-skilled-mcp/SKILL.md) for the complete command reference, argument formats, failure behavior, and safety rules.

### Using prompts and resources

`details` only returns **registered definitions**. To actually use them, `prompt NAME` and `resource URI` first verify the name/URI against the live `/prompts` or `/resources` catalog, then issue one `prompts/get` or `resources/read`. Unregistered URIs are rejected on purpose: the server's `resources/read` also accepts arbitrary `http://` and `file://` URIs, which would bypass the authorization boundary of `request`.

`find` is a purely local, case-insensitive filter over catalog summaries (name, URI, description, tags); it never fetches definitions or content. There is no batch-call command: when several calls are needed, the agent writes ordinary JavaScript with the library API, taking each tool's schema before calling it. Every `call` still re-reads the INI and validates discovery, so sequence, failure handling and replay scope stay under the caller's control.

### Use the client as a library

The script is dual-purpose: the CLI above and a zero-dependency CommonJS library. `require(...)` it and call functions with native objects instead of assembling JSON strings or spawning a process — useful when an agent wants to script a multi-step workflow itself. Library calls go through the same `execute()` path as the CLI, so INI/discovery validation, `max_details`, the 64 KiB cap, catalog whitelists, no-retry and exit codes are all preserved.

```js
const unity = require('./scripts/unity_mcp.js');
unity.configure({ timeout: 15 });
const catalog = await unity.listCapabilities('tools');
const { name, args } = pickFrom(catalog, await unity.getSchema(name));
const summary = await unity.workflowContext('TASK', 'DESCRIPTION', async wf => {
  await unity.callSkill(name, args);
  return wf.summary();
});
```

`callSkill` returns the MCP `result`; `callTool` returns the full JSON-RPC envelope; `*Text` / `*Json` helpers extract and parse `content[*].text`. A workflow context only records calls (`wf.calls`, `wf.summary()`), never retries or rolls back — a failed call rejects as usual. `pickFrom` is pseudocode for choosing a tool and its arguments from the live catalog and schema, not a library function. Tool names must come from `find` / `list --kind tools`. `new Client({...})` allows multiple connections; all methods are async and errors are `ClientError` with a `.code`. See [`SKILL.md`](unity-package/Skills/unity-skilled-mcp/SKILL.md) for the full API table and safety rules.

On Windows Git Bash, MSYS rewrites `/ROUTE` arguments into local paths. The client now restores those paths via `EXEPATH` and also accept bare routes (`request groups`); absolute URLs and CMD / PowerShell / POSIX shells are unchanged.

## Tool use is discovery-driven

The Skill does **not** document any concrete Unity tool, action, parameter or mode. Which tools are registered depends on the Unity project, its version, installed packages and the service configuration, and it can change between sessions. A name seen in older docs, examples or source files is not a promise that it exists in the running service.

Every task follows the same order: `discover` → `groups` / `list` / `find` → `details` / `schema` / `schemas` → `call`. Names, URIs, tags, parameters and allowed values must all come from the live service response for that task. If a tool is missing, disabled, or reports that an optional package is not installed, report it and stop — do not guess a replacement name or retry with different arguments.

The package source contains tool categories for engine operations, animation, UI Toolkit, cameras, effects, Test Runner, modeling and C# execution, among others. That inventory is a description of the source tree, **not** a runtime guarantee; see [`SKILL.md`](unity-package/Skills/unity-skilled-mcp/SKILL.md) for the full contract. Optional packages are resolved through reflection, so the package still compiles when they are absent and only the affected tool returns an explicit `not installed` error.

```sh
node scripts/unity_mcp.js discover
node scripts/unity_mcp.js list --kind tools
node scripts/unity_mcp.js schema NAME_FROM_LIST
node scripts/unity_mcp.js call NAME_FROM_LIST --args-file args.json
```

## HTTP discovery API

The Skill clients use these discovery routes on the active Unity service:

| Route | Purpose |
| --- | --- |
| `GET /discovery` | Verify the active service and read non-MCP connection metadata |
| `GET /groups` | List available tags and tool/prompt/resource counts |
| `GET /tools` | List tool names, short descriptions, and tags |
| `GET /prompts` | List prompt names, short descriptions, and tags |
| `GET /resources` | List resource URIs, names, short descriptions, and tags |
| `GET /tools/{name}/schema` | Read one tool's full schema |
| `POST /capabilities/schemas` | Read several tool schemas in one request |
| `POST /capabilities/details` | Read details by one tag or an explicit mixed `members` array |

The details endpoint accepts exactly one selection mode: `{ "tag": "..." }` or `{ "members": [...] }`. It rejects empty or unknown selections, wildcard/all selectors, and requests that cover the complete capability set. Duplicate members are counted once. Resource details describe registered metadata; they do not implicitly read the resource contents.

The schemas endpoint accepts `{ "tools": ["name", ...] }` and returns `{ "items": [{ "name", "description", "inputSchema" }], "count", "max_details" }`, in request order with duplicates counted once. It shares the same `max_details` limit, the 64 KiB response cap, the full-set rejection, and 404 for unknown or disabled tools, so a batch cannot be used to dump every schema at once.

## Existing MCP clients

The Skill workflow is an additional access path, not a replacement for the existing MCP protocol. Current MCP clients can continue using the original operations, including `tools/list`, `tools/call`, `prompts/list`, `prompts/get`, `resources/list`, and `resources/read`, with their existing client configuration. No Skill installation is required for that legacy workflow.

The Skill's discovery requests do not change the behavior of those MCP operations. For the lower-context workflow, use the Skill client rather than asking an agent to connect directly to the MCP transport.

## Tool framework

当前 12 个扩展实现模块采用简短前缀：`scene`、`asset`、`animation`、`ui`、`editor`、`runtime`、`project`、`system`、`physics`、`rendering`、`audio`、`optional_runtime`。其中 `scene` / `ui` / `editor` / `project` / `audio` 合并到已有前缀；旧工具名称和动作不变，不注册 `_workflow` 别名。包内仍为 55 个实现模块、446 个动作；严格参数校验按实际实现模块的 `IStrictToolModule` 标记生效，而不是按前缀生效。

Tools are declared with attributes and discovered purely by reflection — there is no state tree and no generated declaration file. One action equals one tool, named as `prefix.action` (for example `animator.create`, `hierarchy.create_cube`, `gameobject.modify`), each with its own exact `inputSchema`.

The framework lives in `unity-package/Editor/ToolFramework/`:

| File | Responsibility |
| --- | --- |
| `ToolAttributes.cs` | `ToolModuleAttribute` / `ToolActionAttribute` / `ToolParamAttribute` |
| `ToolCatalog.cs` | Reflection scan of every `[ToolModule]` class; the single source of truth |
| `ToolSpec.cs` | Per-tool metadata (name, group, parameters, injections, handler delegate) |
| `ToolSchema.cs` | Builds each tool's exact `inputSchema` |
| `ToolCall.cs` | Executes one tool (a fresh instance per call; no shared dispatcher state) |
| `ToolContext.cs` | Per-call context (`JsonData` + `ObjectReferences`, indexer, `Complete` / `AsyncReturn`) |
| `ToolModuleBase.cs` | Module base class, including the `ITargetedTool` contract |
| `ToolTargetResolver.cs` | Target resolution (`TargetScope { Any, Hierarchy, Project }`) |

A minimal module:

```csharp
[ToolModule("animator", GroupEn = "Animation", GroupZh = "动画与特效")]
public class AnimatorTool : ToolModuleBase
{
    public override string Description => L.T("Manage Animator controllers", "管理动画控制器");

    protected override MethodKey[] CreateKeys() => new MethodKey[]
    {
        new MethodStr("path", L.T("Controller asset path", "控制器资源路径"), false),
    };

    [ToolAction("create", "path=require")]
    private object CreateController(JsonClass args) { /* ... */ }
}
```

Strongly typed handlers are also supported: `public object Create([ToolParam("path")] string path)`.

A module whose handlers operate on an existing object can implement `ITargetedTool`; the framework resolves the target once before the handler runs and stores it under `_resolved_targets`:

```csharp
public object ResolveTarget(ToolContext ctx)
    => ToolTargetResolver.Resolve<GameObject>(ctx, TargetScope.Hierarchy);
```

## Extending the capability set

Add a tool by creating one `XxxTool.cs` file: mark the class with `[ToolModule]`, extend `ToolModuleBase`, implement `Description` and `CreateKeys()`, and mark each handler with `[ToolAction]`. The catalog discovers it by reflection — no registration, no generation, no extra files. Keep descriptions concise so catalog summaries stay useful, and set the module's `GroupEn` / `GroupZh` to control how the tool appears in `groups`.

```text
unity-package/Editor/Connect/       Unity service and discovery implementation
unity-package/Editor/ToolFramework/ Attribute + reflection tool framework
unity-package/Editor/Tools/         Tool modules (one [ToolModule] per real top-level class)
unity-package/Editor/GUI/           MCP status window and controls
unity-package/Editor/Model/         Data models and local service settings
unity-runtime-package/Runtime/      Standalone trimmed runtime package (Player-side)
unity-package/Skills/unity-skilled-mcp/ Skill instructions and the JS client
unity-package/Skills/unity-skilled-mcp/tests/  Client regression tests
```

> The runtime package is a separate assembly aimed at the Player side and keeps its own `IToolMethod` / `StateTreeContext`. It is not affected by the Editor-side removal of the state tree.

## Run the Skill client tests

From `unity-package/Skills/unity-skilled-mcp/`:

```sh
node --test tests/test_unity_mcp.js
```

## Troubleshooting

- **The Skill cannot find a service:** make sure the Unity service is running and `~/.unity-mcp/skill.ini` exists. Restart the service after changing its port.
- **The INI and running service disagree:** restart the intended Unity project's MCP service so it republishes its current settings.
- **A tag has no details:** get the current tag from `groups`; tags are generated from the active capabilities and may change as configuration changes.
- **A detail request is rejected:** reduce the selected group or member list. Check `max_details`; the 64 KiB response limit and full-set rejection still apply.
- **A schema batch is rejected:** send fewer tool names per call. `POST /capabilities/schemas` shares `max_details`, the 64 KiB cap, and full-set rejection; it never splits the request automatically.
- **A capability is not listed:** check whether it is enabled in the Unity MCP settings and whether the target project has completed initialization.
- **A call fails:** inspect Unity's Console and the MCP Status window's request records; confirm the argument JSON matches the selected definition.

---

For additional project information and examples, see the [Chinese README](README.zh-CN.md), the [project website](docs/index.html), and the [Unity Skill](unity-package/Skills/unity-skilled-mcp/SKILL.md).
