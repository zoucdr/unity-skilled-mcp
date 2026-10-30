[English](README.md) | [中文](README.zh-CN.md)

---

# Unity Skilled MCP

**A lightweight Skill + MCP workflow for controlling the Unity Editor with AI.**

Unity Skilled MCP runs its service inside the Unity Editor and exposes a large set of Unity development capabilities. The lightweight Skill keeps the agent's initial context small: it discovers short capability summaries first, loads only the relevant definitions when needed, and delegates execution to the Unity service.

![Unity Skilled MCP](docs/unity3d-mcp.png)

## Why Skill + MCP?

- **Progressive discovery:** start with capability names, short descriptions, and tags; retrieve full schemas only when needed.
- **Tools, prompts, and resources:** browse each catalog, retrieve a tagged group, or request a task-specific mixed set.
- **Automatic local connection discovery:** Unity writes the active service information to `~/.unity-mcp/skill.ini` whenever the service starts successfully.
- **Configurable detail limits:** the per-request item limit is configurable in Unity; the response is also capped at 64 KiB, and full-catalog detail requests are rejected.
- **JavaScript or Python:** the Skill includes equivalent dependency-free clients. JavaScript is the default.
- **Legacy MCP compatibility:** existing clients can continue using the original MCP list, call, prompt, and resource operations.
- **Unity-safe execution:** editor operations are dispatched through the Unity Editor's main-thread execution flow.

## Architecture

```text
AI agent
  └─ Unity Skill (SKILL.md + unity_mcp.js / unity_mcp.py)
       ├─ Reads ~/.unity-mcp/skill.ini and validates the active service
       ├─ Discovers short summaries and selected capability definitions over HTTP
       └─ Invokes the requested operation through the client script
            └─ Unity Editor service → main-thread execution → Unity APIs

Existing MCP clients ────────────────────────────────┘
```

The Skill is the recommended path for agents that support local Skills. Do not have an agent bypass the Skill and construct MCP requests itself. The INI intentionally does **not** contain an MCP endpoint field.

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

- Copy `unity-package/Skills/unity3d-skill/` into the agent's Skills directory, keeping the folder name `unity3d-skill`.
- Or use the packaged archive at `dist/unity3d-skill.zip`.

### 4. Use it from the agent

Ask the agent to use the Unity Skill, then describe the Unity task in natural language. The Skill guides the agent to verify the connection, discover the relevant capability, read its definition, and invoke it.

## Unity settings and connection file

The MCP Status window exposes the client runtime and the maximum number of capability details returned by one request. These settings are published on the next successful service start.

| Setting | Values | Default / behavior |
| --- | --- | --- |
| Service port | Valid port number | `8000` |
| Client script language | `js` or `python` | `js`; selects the client runtime the Skill should use |
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
script_language=js
max_details=32
```

The file contains local connection metadata and runtime settings, but **does not publish `mcp_url`**. The Skill client reads the file dynamically on each command and validates the service before proceeding; it does not scan ports or automatically replay failed write operations.

## How progressive discovery works

1. **Verify the service:** read the INI and check the current service's discovery metadata.
2. **Browse summaries:** read groups or a specific tools, prompts, or resources catalog.
3. **Load only what is needed:** read one tool schema, request a fixed tag group, or submit a task-specific list of members.
4. **Execute:** call the selected operation through the Skill client.

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

# Load a single tool definition, then call it
node scripts/unity_mcp.js schema hierarchy_search
node scripts/unity_mcp.js call hierarchy_search --args-file args.json
```

The tag in `details --tag` must come from the live group catalog. A `selection.json` file contains the members array itself, for example:

```json
[
  { "kind": "tool", "name": "hierarchy_search" },
  { "kind": "prompt", "name": "replace-with-a-listed-prompt" },
  { "kind": "resource", "uri": "replace-with-a-listed-resource-uri" }
]
```

Set `script_language=python` in Unity to use the equivalent Python client instead:

```sh
python scripts/unity_mcp.py discover
python scripts/unity_mcp.py groups
python scripts/unity_mcp.py details --tag hierarchy-management
python scripts/unity_mcp.py schema hierarchy_search
python scripts/unity_mcp.py call hierarchy_search --args-file args.json
```

The clients require no third-party packages: Node.js 18+ or Python 3.8+ is required, according to the selected runtime. Consult [`SKILL.md`](unity-package/Skills/unity3d-skill/SKILL.md) for the complete command reference, argument formats, failure behavior, and safety rules.

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
| `POST /capabilities/details` | Read details by one tag or an explicit mixed `members` array |

The details endpoint accepts exactly one selection mode: `{ "tag": "..." }` or `{ "members": [...] }`. It rejects empty or unknown selections, wildcard/all selectors, and requests that cover the complete capability set. Duplicate members are counted once. Resource details describe registered metadata; they do not implicitly read the resource contents.

## Existing MCP clients

The Skill workflow is an additional access path, not a replacement for the existing MCP protocol. Current MCP clients can continue using the original operations, including `tools/list`, `tools/call`, `prompts/list`, `prompts/get`, `resources/list`, and `resources/read`, with their existing client configuration. No Skill installation is required for that legacy workflow.

The Skill's discovery requests do not change the behavior of those MCP operations. For the lower-context workflow, use the Skill client rather than asking an agent to connect directly to the MCP transport.

## Extending the capability set

The Unity package discovers registered tools, prompts, and resources and exposes their summaries and definitions through the discovery API. Tool grouping is represented by tags; prompts and resources can also be organized with capability tags. Keep descriptions concise so catalog summaries remain useful, and add or adjust tags when a capability should be discoverable as part of a group.

Relevant implementation areas:

```text
unity-package/Editor/Connect/       Unity service and discovery implementation
unity-package/Editor/GUI/           MCP status window and controls
unity-package/Editor/Model/         Local service settings
unity-package/Skills/unity3d-skill/ Skill instructions and JS/Python clients
unity-package/Skills/unity3d-skill/tests/  Client regression tests
```

## Run the Skill client tests

From `unity-package/Skills/unity3d-skill/`:

```sh
node --test tests/test_unity_mcp.js
python -B -m unittest discover -s tests -p 'test_*.py'
```

## Troubleshooting

- **The Skill cannot find a service:** make sure the Unity service is running and `~/.unity-mcp/skill.ini` exists. Restart the service after changing its port.
- **The INI and running service disagree:** restart the intended Unity project's MCP service so it republishes its current settings.
- **A tag has no details:** get the current tag from `groups`; tags are generated from the active capabilities and may change as configuration changes.
- **A detail request is rejected:** reduce the selected group or member list. Check `max_details`; the 64 KiB response limit and full-set rejection still apply.
- **A capability is not listed:** check whether it is enabled in the Unity MCP settings and whether the target project has completed initialization.
- **A call fails:** inspect Unity's Console and the MCP Status window's request records; confirm the argument JSON matches the selected definition.

---

For additional project information and examples, see the [Chinese README](README.zh-CN.md), the [project website](docs/index.html), and the [Unity Skill](unity-package/Skills/unity3d-skill/SKILL.md).
