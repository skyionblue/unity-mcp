"""Tests for gateway tools: search_tools, execute_tool, execute_tools."""
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

import services.tools.gateway as gateway_module
import services.tools.manage_gameobject  # ensure grouped tool is registered
from services.registry import get_registered_tools, clear_tool_registry, mcp_for_unity_tool
import services.registry.tool_registry as tool_registry_module


@pytest.fixture(autouse=True)
def restore_registry():
    original = list(tool_registry_module._tool_registry)
    yield
    tool_registry_module._tool_registry[:] = original


def _make_ctx(rules=None):
    ctx = AsyncMock()
    ctx._get_visibility_rules = AsyncMock(return_value=rules or [])
    return ctx


class TestSearchTools:
    @pytest.mark.asyncio
    async def test_wildcard_returns_all_grouped_tools(self):
        ctx = _make_ctx()
        result = await gateway_module.search_tools(ctx, query="*")
        assert "tools" in result
        assert result["count"] == len(result["tools"])
        # All returned tools must have a group (meta-tools excluded)
        for t in result["tools"]:
            assert t["group"] is not None

    @pytest.mark.asyncio
    async def test_empty_query_same_as_wildcard(self):
        ctx = _make_ctx()
        result_star = await gateway_module.search_tools(ctx, query="*")
        result_empty = await gateway_module.search_tools(ctx, query="")
        assert result_star["count"] == result_empty["count"]

    @pytest.mark.asyncio
    async def test_keyword_filters_by_name_or_description(self):
        ctx = _make_ctx()
        result = await gateway_module.search_tools(ctx, query="gameobject")
        names = [t["name"] for t in result["tools"]]
        assert any("gameobject" in n for n in names)

    @pytest.mark.asyncio
    async def test_group_filter_narrows_results(self):
        ctx = _make_ctx()
        all_result = await gateway_module.search_tools(ctx, query="*")
        core_result = await gateway_module.search_tools(ctx, query="*", group="core")
        assert core_result["count"] <= all_result["count"]
        for t in core_result["tools"]:
            assert t["group"] == "core"

    @pytest.mark.asyncio
    async def test_no_match_returns_empty(self):
        ctx = _make_ctx()
        result = await gateway_module.search_tools(ctx, query="xyzzy_no_match_12345")
        assert result["count"] == 0
        assert result["tools"] == []

    @pytest.mark.asyncio
    async def test_group_enabled_reflects_session_rules(self):
        ctx = _make_ctx(rules=[{"tags": ["group:animation"], "enabled": True}])
        result = await gateway_module.search_tools(ctx, query="*", group="animation")
        for t in result["tools"]:
            assert t["group_enabled"] is True

    @pytest.mark.asyncio
    async def test_meta_tools_excluded_from_results(self):
        ctx = _make_ctx()
        result = await gateway_module.search_tools(ctx, query="*")
        meta_names = {"search_tools", "execute_tool", "execute_tools", "manage_tools"}
        for t in result["tools"]:
            assert t["name"] not in meta_names


class TestExecuteTool:
    @pytest.mark.asyncio
    async def test_unknown_tool_returns_error(self):
        ctx = _make_ctx()
        result = await gateway_module.execute_tool(ctx, tool="no_such_tool_xyz")
        assert result.get("success") is False
        assert "not found" in result.get("error", "").lower()

    @pytest.mark.asyncio
    async def test_dispatches_to_registered_handler(self):
        # Register a lightweight fake tool and confirm execute_tool calls it.
        called_with = {}

        @mcp_for_unity_tool(description="test dispatch", group="core")
        async def _test_dispatch_tool(ctx, value: str = "default"):
            called_with["value"] = value
            return {"success": True, "value": value}

        ctx = _make_ctx()
        result = await gateway_module.execute_tool(ctx, tool="_test_dispatch_tool", params={"value": "hello"})
        assert result.get("success") is True
        assert called_with.get("value") == "hello"

    @pytest.mark.asyncio
    async def test_bad_params_returns_error_not_exception(self):
        @mcp_for_unity_tool(description="strict params", group="core")
        async def _strict_tool(ctx, required_param: str):
            return {"ok": True}

        ctx = _make_ctx()
        # Pass the wrong param name
        result = await gateway_module.execute_tool(ctx, tool="_strict_tool", params={"wrong_key": "x"})
        assert result.get("success") is False
        assert "parameter" in result.get("error", "").lower()

    @pytest.mark.asyncio
    async def test_none_params_treated_as_empty(self):
        @mcp_for_unity_tool(description="no params", group="core")
        async def _no_param_tool(ctx):
            return {"success": True}

        ctx = _make_ctx()
        result = await gateway_module.execute_tool(ctx, tool="_no_param_tool", params=None)
        assert result.get("success") is True


class TestExecuteTools:
    @pytest.mark.asyncio
    async def test_empty_calls_returns_empty_results(self):
        ctx = _make_ctx()
        result = await gateway_module.execute_tools(ctx, calls=[])
        assert result["count"] == 0
        assert result["results"] == []

    @pytest.mark.asyncio
    async def test_missing_tool_key_returns_per_call_error(self):
        ctx = _make_ctx()
        result = await gateway_module.execute_tools(ctx, calls=[{"params": {}}])
        assert result["count"] == 1
        assert result["results"][0]["result"].get("success") is False

    @pytest.mark.asyncio
    async def test_multiple_calls_run_in_order(self):
        order = []

        @mcp_for_unity_tool(description="order A", group="core")
        async def _order_a(ctx):
            order.append("a")
            return {"step": "a"}

        @mcp_for_unity_tool(description="order B", group="core")
        async def _order_b(ctx):
            order.append("b")
            return {"step": "b"}

        ctx = _make_ctx()
        result = await gateway_module.execute_tools(
            ctx,
            calls=[{"tool": "_order_a"}, {"tool": "_order_b"}],
        )
        assert result["count"] == 2
        assert [r["tool"] for r in result["results"]] == ["_order_a", "_order_b"]
        assert order == ["a", "b"]

    @pytest.mark.asyncio
    async def test_unknown_tool_in_batch_does_not_abort_rest(self):
        @mcp_for_unity_tool(description="always ok", group="core")
        async def _always_ok(ctx):
            return {"success": True}

        ctx = _make_ctx()
        result = await gateway_module.execute_tools(
            ctx,
            calls=[
                {"tool": "no_such_tool_abc"},
                {"tool": "_always_ok"},
            ],
        )
        assert result["count"] == 2
        assert result["results"][0]["result"].get("success") is False
        assert result["results"][1]["result"].get("success") is True
