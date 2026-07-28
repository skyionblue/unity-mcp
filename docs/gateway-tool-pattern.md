# Gateway Tool Pattern for DevUnityMCP

## Problem

The DevUnityMCP MCP server currently registers ~48 tools directly with FastMCP. All of them appear as individual deferred tools in the AI client's context window on every session, even before any tool schema is loaded. This creates a large, noisy context footprint that grows as new tool groups are added.

## Goal

Expose exactly **3 tools** to the AI client. All 48 underlying tools remain as internal handlers but are not registered with FastMCP directly. The AI discovers and invokes them through the 3 gateway tools.

---

## The 3 Gateway Tools

### `search_tools`
```
search_tools(query: str, group?: str) → list[{name, description, group}]
```
Searches available Unity MCP tools by keyword. Optionally filter by group name. Returns tool names, descriptions, and group membership. Does **not** require a Unity connection — it only reads the server-side registry.

### `execute_tool`
```
execute_tool(tool: str, params?: dict) → any
```
Calls a single Unity MCP tool by its exact name. `params` is passed directly to the underlying handler. Returns the same response the tool would have returned if called directly.

### `execute_tools`
```
execute_tools(calls: list[{tool: str, params?: dict}]) → list[any]
```
Batch version. Executes multiple tools in sequence (not parallel — Unity's main thread is single-threaded). Returns a list of responses in call order.

---

## What Changes

### `Server/src/services/registry/tool_registry.py`
The `mcp_for_unity_tool` decorator currently registers tools with the FastMCP instance. In gateway mode, it instead registers them in an internal dict (`_tool_registry`) and does **not** call `mcp.tool()`. The registry remains as the source of truth for discovery.

No change to how groups or activation/deactivation work — that logic stays in `manage_tools.py` and still gates which tools are callable at runtime.

### `Server/src/services/tools/gateway.py` *(new file)*
Implements the 3 gateway tools using `@mcp_for_unity_tool` with `group=None` (always visible meta-tools, like `manage_tools` and `set_active_instance` already are):

```python
@mcp_for_unity_tool(description="Search available Unity MCP tools by keyword.", group=None)
async def search_tools(ctx, query, group=None): ...

@mcp_for_unity_tool(description="Execute a single Unity MCP tool by name.", group=None)
async def execute_tool(ctx, tool, params=None): ...

@mcp_for_unity_tool(description="Execute multiple Unity MCP tools in sequence.", group=None)
async def execute_tools(ctx, calls): ...
```

`execute_tool` calls into `_tool_registry` to find the handler, checks that the tool's group is currently active, injects the `ctx` and Unity instance, then invokes the handler directly (same path as a direct MCP call).

### `Server/src/main.py`
`register_all_tools()` changes in one place: instead of calling `mcp.tool()` for all discovered handlers, it only calls `mcp.tool()` for the 3 gateway tools plus any tools explicitly flagged `gateway=False` (for backwards-compatibility escape hatch if needed).

---

## What Does NOT Change

- **CLI commands** (`Server/src/cli/commands/`) — these call the Python handler functions directly and are unaffected.
- **Unity C# side** — the C# `HandleCommand` methods are unchanged.
- **Group system** — `manage_tools` still activates/deactivates groups. `execute_tool` enforces group activation (returns an error if the tool's group is disabled, same as the current `preflight()` check).
- **`set_active_instance`, `manage_tools`, `debug_request_context`** — these meta-tools stay directly registered since they don't route to Unity and are needed for session management. They would remain alongside the 3 gateway tools, bringing the total to ~6 visible tools.

---

## Context Impact

| Before | After |
|--------|-------|
| ~48 deferred tool entries in every session | ~6 tool entries (3 gateway + 3 meta) |
| Schema loaded on first use of each tool | Schema loaded once for 3 tools |
| AI must be told which tools exist | AI calls `search_tools` to discover what's available |

---

## Migration Notes

- Existing prompts and skills that call `mcp__DevUnityMCP__manage_gameobject` etc. by name will need to use `execute_tool(tool="manage_gameobject", params={...})` instead.
- The CLAUDE.md and `unity-mcp-skill` skill will need to be updated to document the new calling convention.
- The `unity_docs` and `unity_reflect` tools (currently in the `docs` group) follow the same pattern — discovered via `search_tools`, called via `execute_tool`.

---

## Implementation Order

1. Extract `_tool_registry` dispatch logic into a shared `invoke_tool(name, params, ctx)` helper
2. Add `Server/src/services/tools/gateway.py` with the 3 tools
3. Change `register_all_tools()` to skip direct registration for non-meta tools
4. Update `manage_tools.py` so `activate`/`deactivate` still work (they mutate the same registry)
5. Update `unity-mcp-skill` and CLAUDE.md calling conventions
6. Test: run the existing Python test suite; add tests for `search_tools`/`execute_tool`/`execute_tools`
