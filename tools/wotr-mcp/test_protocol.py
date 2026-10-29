import asyncio
import sys
import unittest
from pathlib import Path

from mcp.client.session import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client


SERVER = Path(__file__).resolve().parent / "server.py"


class ProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_stdio_initialization_and_tool_discovery(self):
        parameters = StdioServerParameters(
            command=sys.executable,
            args=[str(SERVER)],
            cwd=str(SERVER.parent),
        )
        async with stdio_client(parameters) as (read_stream, write_stream):
            async with ClientSession(read_stream, write_stream) as session:
                await session.initialize()
                tools = await session.list_tools()

        names = {tool.name for tool in tools.tools}
        self.assertEqual(
            {
                "get_state",
                "advance_startup",
                "create_host",
                "connect_client",
                "set_ready",
                "start_game",
                "assign_character",
                "teleport_character",
                "start_nearest_combat",
                "compare_instances",
                "set_pause",
                "set_combat_mode",
                "force_combat_sync",
                "save_combat",
                "load_save",
                "list_saves",
                "wait_for_state",
            },
            names,
        )


if __name__ == "__main__":
    unittest.main()
