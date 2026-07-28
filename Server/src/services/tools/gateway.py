"""
Gateway tools — search_tools, execute_tool, execute_tools.

In gateway mode (UNITY_MCP_GATEWAY_MODE=1, the default) these three tools are
the *only* grouped tools registered with FastMCP.  All ~48 domain tools are
accessible through them instead of being listed individually.

This keeps the MCP tool listing lean (3 tools instead of 48+) while preserving
full capability discovery and dispatch.
"""
from __future__ import annotations

import logging
from typing import Annotated, Any

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import (
    mcp_for_unity_tool,
    get_registered_tools,
    get_tool_by_name,
    TOOL_GROUPS,
    DEFAULT_ENABLED_GROUPS,
)

logger = logging.getLogger("mcp-for-unity-server")


# ---------------------------------------------------------------------------
# Internal dispatch helper
# ---------------------------------------------------------------------------

async def _dispatch(ctx: Context, tool_name: str, params: dict[str, Any] | None) -> Any:
    """Look up a tool by name in the registry and call its handler."""
    tool_info = get_tool_by_name(tool_name)
    if tool_info is None:
        available = [t["name"] for t in get_registered_tools() if t.get("group") is not None]
        return {
            "success": False,
            "error": f"Tool '{tool_name}' not found.",
            "hint": "Use search_tools to discover available tools.",
            "available_count": len(available),
        }

    func = tool_info["func"]
    try:
        result = await func(ctx, **(params or {}))
        return result
    except TypeError as exc:
        # Surface parameter mismatch clearly rather than crashing.
        return {
            "success": False,
            "error": f"Parameter error calling '{tool_name}': {exc}",
            "hint": "Use search_tools to check the tool description for required parameters.",
        }


# ---------------------------------------------------------------------------
# Gateway tools — registered with group=None (always visible)
# ---------------------------------------------------------------------------

@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        "Search available Unity MCP tools by keyword. "
        "Returns tool names, descriptions, and groups. "
        "Pass an empty query or '*' to list all tools. "
        "Filter by group name to narrow results (e.g. group='core', group='animation'). "
        "Use this to discover which tool to call before using execute_tool."
    ),
    annotations=ToolAnnotations(title="Search Tools", readOnlyHint=True),
)
async def search_tools(
    ctx: Context,
    query: Annotated[
        str,
        "Keyword to search for in tool names and descriptions. Pass '*' or '' to list all."
    ] = "*",
    group: Annotated[
        str | None,
        "Optional group filter. Valid groups: " + ", ".join(sorted(TOOL_GROUPS.keys()))
    ] = None,
) -> dict[str, Any]:
    q = query.strip().lower()
    all_q = q in ("", "*")

    # Determine which groups are currently enabled in this session.
    session_enabled: dict[str, bool] = {}
    try:
        rules = await ctx._get_visibility_rules()
        for rule in rules:
            tags = rule.get("tags") or []
            enabled = rule.get("enabled", True)
            for tag in tags:
                if isinstance(tag, str) and tag.startswith("group:"):
                    session_enabled[tag[len("group:"):]] = enabled
    except Exception:
        pass

    def group_enabled(g: str | None) -> bool:
        if g is None:
            return True
        if g in session_enabled:
            return session_enabled[g]
        return g in DEFAULT_ENABLED_GROUPS

    results = []
    for tool in get_registered_tools():
        tool_group = tool.get("group")
        if tool_group is None:
            continue  # skip meta-tools (search/execute/manage_tools etc.)

        if group and tool_group != group:
            continue

        name = tool["name"]
        desc = tool.get("description") or ""
        if not all_q and q not in name.lower() and q not in desc.lower():
            continue

        results.append({
            "name": name,
            "description": desc,
            "group": tool_group,
            "group_enabled": group_enabled(tool_group),
        })

    results.sort(key=lambda t: (not t["group_enabled"], t["group"], t["name"]))
    return {
        "tools": results,
        "count": len(results),
        "note": (
            "group_enabled reflects the current session state. "
            "Use manage_tools to activate a group, then call execute_tool."
        ),
    }


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        "Execute a single Unity MCP tool by name. "
        "Use search_tools first to find the right tool name and understand its parameters. "
        "params keys must match the tool's parameter names exactly (snake_case). "
        "Example: execute_tool(tool='manage_gameobject', params={'action': 'create', 'name': 'Cube'})"
    ),
    annotations=ToolAnnotations(title="Execute Tool", readOnlyHint=False),
)
async def execute_tool(
    ctx: Context,
    tool: Annotated[str, "Exact tool name to call (e.g. 'manage_gameobject', 'manage_scene')."],
    params: Annotated[
        dict[str, Any] | None,
        "Parameters for the tool as a dict. Keys are snake_case parameter names."
    ] = None,
) -> Any:
    return await _dispatch(ctx, tool, params)


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        "Execute multiple Unity MCP tools in sequence. "
        "Each call is an object with 'tool' (name) and optional 'params' (dict). "
        "Calls run sequentially — Unity's main thread is single-threaded. "
        "Returns a list of results in call order, each wrapped with the tool name."
    ),
    annotations=ToolAnnotations(title="Execute Tools", readOnlyHint=False),
)
async def execute_tools(
    ctx: Context,
    calls: Annotated[
        list[dict[str, Any]],
        "List of {tool: str, params?: dict} objects to execute in order."
    ],
) -> dict[str, Any]:
    if not calls:
        return {"results": [], "count": 0}

    results = []
    for call in calls:
        tool_name = call.get("tool", "")
        params = call.get("params") or {}
        if not tool_name:
            results.append({"tool": "", "result": {"success": False, "error": "'tool' key is required in each call"}})
            continue
        result = await _dispatch(ctx, tool_name, params)
        results.append({"tool": tool_name, "result": result})

    return {"results": results, "count": len(results)}
