"""Run a live MCP smoke check against the two isolated game instances."""

import asyncio
import json
import sys
from pathlib import Path

from mcp.client.session import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client


SERVER = Path(__file__).resolve().parent / "server.py"


async def main() -> None:
    parameters = StdioServerParameters(
        command=sys.executable,
        args=[str(SERVER)],
        cwd=str(SERVER.parent),
    )
    async with stdio_client(parameters) as (read_stream, write_stream):
        async with ClientSession(read_stream, write_stream) as session:
            await session.initialize()
            tools = await session.list_tools()
            host = await session.call_tool("get_state", {"instance": "host"})
            client = await session.call_tool("get_state", {"instance": "client"})
            saves = await session.call_tool("list_saves", {"instance": "host"})
    print(json.dumps({
        "tools": [tool.name for tool in tools.tools],
        "host": host.structured_content,
        "client": client.structured_content,
        "hostSaves": saves.structured_content,
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
