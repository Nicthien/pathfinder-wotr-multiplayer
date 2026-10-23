"""Local MCP server for the isolated WotR multiplayer test bridge."""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import time
import uuid
import zipfile
from pathlib import Path
from typing import Any

from mcp.server.mcpserver import MCPServer


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PROFILES = {
    "host": ROOT / "local-artifacts" / "combat-save-lab" / "host-profile",
    "client": ROOT / "local-artifacts" / "combat-save-lab" / "client-profile",
}


def _profile(instance: str) -> Path:
    key = instance.strip().lower()
    if key not in DEFAULT_PROFILES:
        raise ValueError("instance must be 'host' or 'client'")
    override = os.environ.get(f"WOTR_MCP_{key.upper()}_PROFILE")
    profile = Path(override).expanduser().resolve() if override else DEFAULT_PROFILES[key].resolve()
    if not (profile / ".rtwp-test-profile").is_file():
        raise RuntimeError(f"isolated test-profile marker missing: {profile}")
    return profile


def _read_json(path: Path) -> dict[str, Any]:
    last_error: Exception | None = None
    # Unity writes atomically, but antivirus/indexing on Windows can retain a
    # just-renamed response handle for noticeably longer than one scheduler
    # tick. Keep polling the same authenticated response instead of turning a
    # transient sharing violation into a failed game operation.
    for _ in range(30):
        try:
            return json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError) as exc:
            last_error = exc
            time.sleep(0.1)
    raise RuntimeError(f"unable to read {path}: {last_error}")


def _ready(instance: str) -> tuple[Path, dict[str, Any]]:
    profile = _profile(instance)
    path = profile / "McpBridge" / "ready.json"
    if not path.is_file():
        raise RuntimeError(
            f"the {instance} bridge is not running; launch the isolated instance with -Automation"
        )
    ready = _read_json(path)
    if Path(str(ready.get("profile", ""))).resolve() != profile:
        raise RuntimeError(f"bridge profile mismatch for {instance}")
    if not ready.get("sessionId") or not ready.get("token"):
        raise RuntimeError(f"invalid bridge readiness document for {instance}")
    return profile, ready


def _call(instance: str, command: str, arguments: dict[str, Any] | None = None,
          timeout_seconds: float = 30.0) -> dict[str, Any]:
    profile, ready = _ready(instance)
    bridge = profile / "McpBridge"
    requests = bridge / "requests"
    responses = bridge / "responses"
    requests.mkdir(parents=True, exist_ok=True)
    responses.mkdir(parents=True, exist_ok=True)
    request_id = uuid.uuid4().hex
    request = {
        "id": request_id,
        "sessionId": ready["sessionId"],
        "token": ready["token"],
        "command": command,
        "arguments": arguments or {},
        "expiresUnixMs": int((time.time() + timeout_seconds) * 1000),
    }
    request_path = requests / f"{request_id}.json"
    temporary = requests / f"{request_id}.{uuid.uuid4().hex}.tmp"
    temporary.write_text(json.dumps(request, separators=(",", ":")), encoding="utf-8")
    temporary.replace(request_path)
    response_path = responses / f"{request_id}.json"
    deadline = time.monotonic() + max(0.5, min(timeout_seconds, 180.0))
    try:
        while time.monotonic() < deadline:
            if response_path.is_file():
                response = _read_json(response_path)
                response_path.unlink(missing_ok=True)
                if response.get("sessionId") != ready["sessionId"]:
                    raise RuntimeError("the game bridge restarted while the command was running")
                if not response.get("ok"):
                    raise RuntimeError(str(response.get("error") or "game command failed"))
                return response.get("result") or {}
            current = _read_json(bridge / "ready.json")
            if current.get("sessionId") != ready["sessionId"]:
                raise RuntimeError("the game bridge restarted while waiting for a response")
            time.sleep(0.1)
        raise TimeoutError(f"game command timed out after {timeout_seconds:.1f}s: {command}")
    finally:
        _try_unlink(request_path)
        _try_unlink(temporary)


def _try_unlink(path: Path) -> None:
    for _ in range(5):
        try:
            path.unlink(missing_ok=True)
            return
        except PermissionError:
            time.sleep(0.05)


def _field(state: dict[str, Any], dotted_path: str) -> Any:
    value: Any = state
    for part in dotted_path.split("."):
        if not isinstance(value, dict) or part not in value:
            raise KeyError(f"state field not found: {dotted_path}")
        value = value[part]
    return value


def _compare_states(host: dict[str, Any], client: dict[str, Any], position_tolerance: float = 0.75) -> dict[str, Any]:
    differences: list[dict[str, Any]] = []
    for field in (
        "gameId", "stage", "areaId", "isPaused", "isInCombat", "networkCombatStage", "combatMode", "combatPhase",
        "combatEpoch", "combatHash", "pausePolicy", "pausedPlayers", "resumeVotes",
    ):
        if host.get(field) != client.get(field):
            differences.append({"field": field, "host": host.get(field), "client": client.get(field)})

    host_party = {unit.get("id"): unit for unit in host.get("party", []) if unit.get("id")}
    client_party = {unit.get("id"): unit for unit in client.get("party", []) if unit.get("id")}
    if set(host_party) != set(client_party):
        differences.append({
            "field": "party.ids",
            "hostOnly": sorted(set(host_party) - set(client_party)),
            "clientOnly": sorted(set(client_party) - set(host_party)),
        })
    for unit_id in sorted(set(host_party) & set(client_party)):
        left, right = host_party[unit_id], client_party[unit_id]
        for field in ("hp", "maxHp", "isInCombat"):
            if left.get(field) != right.get(field):
                differences.append({
                    "field": f"party.{unit_id}.{field}",
                    "host": left.get(field),
                    "client": right.get(field),
                })
        left_position, right_position = left.get("position") or {}, right.get("position") or {}
        distance_sq = sum(
            (float(left_position.get(axis, 0.0)) - float(right_position.get(axis, 0.0))) ** 2
            for axis in ("x", "y", "z")
        )
        if distance_sq > position_tolerance ** 2:
            differences.append({
                "field": f"party.{unit_id}.position",
                "host": left_position,
                "client": right_position,
                "distance": distance_sq ** 0.5,
            })
    return {"converged": not differences, "differences": differences}


def _list_saves(instance: str) -> dict[str, Any]:
    saves = _profile(instance) / "Saved Games"
    result: list[dict[str, Any]] = []
    for path in sorted(saves.glob("*.zks"), key=lambda item: item.stat().st_mtime, reverse=True):
        item: dict[str, Any] = {
            "fileName": path.name,
            "bytes": path.stat().st_size,
            "modifiedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(path.stat().st_mtime)),
            "combatSave": False,
        }
        try:
            with zipfile.ZipFile(path) as archive:
                if "header.json" in archive.namelist():
                    header = json.loads(archive.read("header.json").decode("utf-8-sig"))
                    item.update({"name": header.get("Name"), "areaId": header.get("Area")})
                if "multiplayer-combat.state" in archive.namelist():
                    metadata = json.loads(archive.read("multiplayer-combat.state").decode("utf-8-sig"))
                    item.update({
                        "combatSave": True,
                        "protocolVersion": metadata.get("Protocol") or metadata.get("protocol"),
                        "buildFingerprint": metadata.get("BuildFingerprint") or metadata.get("buildFingerprint"),
                    })
        except (OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as exc:
            item["inspectionError"] = str(exc)
        result.append(item)
    return {"instance": instance, "profile": str(_profile(instance)), "saves": result}


server = MCPServer(
    name="wotr-multiplayer-test",
    title="WotR Multiplayer Test Control",
    description="Controls and inspects two isolated Pathfinder WotR multiplayer laboratory instances.",
    instructions=(
        "Use only with the marked host/client laboratory profiles. Start with get_state and compare_instances. "
        "Never report combat behavior as validated unless both instances expose matching live combat state."
    ),
)


@server.tool()
async def get_state(instance: str) -> dict[str, Any]:
    """Read the live host or client state, including multiplayer, combat, pause, party positions and HP."""
    return await asyncio.to_thread(_call, instance, "get_state", {}, 30.0)


@server.tool()
async def advance_startup(instance: str) -> dict[str, Any]:
    """Advance the active splash screen in an isolated automated instance."""
    return await asyncio.to_thread(_call, instance, "advance_startup", {}, 30.0)


@server.tool()
async def create_host(file_name: str) -> dict[str, Any]:
    """Create a local host lobby from one save in the isolated host profile."""
    return await asyncio.to_thread(_call, "host", "create_host", {"fileName": file_name}, 30.0)


@server.tool()
async def connect_client(address: str, port: int) -> dict[str, Any]:
    """Connect the isolated client instance to the host's local endpoint."""
    return await asyncio.to_thread(
        _call, "client", "connect_client", {"address": address, "port": port}, 30.0
    )


@server.tool()
async def set_ready(instance: str, ready: bool = True) -> dict[str, Any]:
    """Set the local host or client lobby readiness state."""
    return await asyncio.to_thread(_call, instance, "set_ready", {"ready": ready}, 30.0)


@server.tool()
async def start_game() -> dict[str, Any]:
    """Start the ready isolated two-player lobby and transfer/load the selected save."""
    return await asyncio.to_thread(_call, "host", "start_game", {}, 30.0)


@server.tool()
async def assign_character(index: int) -> dict[str, Any]:
    """Assign one indexed party character to the connected laboratory client."""
    return await asyncio.to_thread(_call, "host", "assign_character", {"index": index}, 30.0)


@server.tool()
async def teleport_character(index: int, distance: float) -> dict[str, Any]:
    """Move one host-world party character for the isolated regroup test."""
    return await asyncio.to_thread(
        _call, "host", "teleport_character", {"index": index, "distance": distance}, 30.0
    )


@server.tool()
async def start_nearest_combat(instance: str = "host", enemy_id: str = "") -> dict[str, Any]:
    """Start local native combat against a matching hostile on one isolated instance."""
    arguments = {"enemyId": enemy_id} if enemy_id else {}
    return await asyncio.to_thread(_call, instance, "start_nearest_combat", arguments, 30.0)


@server.tool()
async def compare_instances(position_tolerance: float = 0.75) -> dict[str, Any]:
    """Compare live host/client area, pause, combat and party state and report every divergence."""
    host, client = await asyncio.gather(get_state("host"), get_state("client"))
    comparison = _compare_states(host, client, position_tolerance)
    comparison.update({"host": host, "client": client})
    return comparison


@server.tool()
async def set_pause(instance: str, paused: bool) -> dict[str, Any]:
    """Request pause or resume and wait for the host and client to expose that state."""
    request = await asyncio.to_thread(_call, instance, "set_pause", {"paused": paused}, 30.0)
    deadline = time.monotonic() + 30.0
    comparison: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        comparison = await compare_instances()
        if (comparison["host"].get("isPaused") == paused
                and comparison["client"].get("isPaused") == paused):
            return {"request": request, "converged": True, "comparison": comparison}
        await asyncio.sleep(0.25)
    return {"request": request, "converged": False, "comparison": comparison}


@server.tool()
async def force_unpause(instance: str) -> dict[str, Any]:
    """Clear a stale local pause in one isolated lab instance before a scenario starts."""
    return await asyncio.to_thread(_call, instance, "force_unpause", {}, 30.0)


@server.tool()
async def set_combat_mode(mode: str) -> dict[str, Any]:
    """Ask the host to switch combat mode. Mode must be real_time or turn_based."""
    return await asyncio.to_thread(_call, "host", "set_combat_mode", {"mode": mode}, 90.0)


@server.tool()
async def force_combat_sync() -> dict[str, Any]:
    """Ask the host to run the mod's normal forced combat synchronization workflow."""
    return await asyncio.to_thread(_call, "host", "force_sync", {}, 90.0)


@server.tool()
async def save_combat() -> dict[str, Any]:
    """Ask the host to create a combat save through the mod's synchronized save workflow."""
    return await asyncio.to_thread(_call, "host", "save_combat", {}, 120.0)


@server.tool()
async def load_save(file_name: str) -> dict[str, Any]:
    """Ask the host to load one .zks file from the isolated host profile and synchronize the session."""
    return await asyncio.to_thread(_call, "host", "load_save", {"fileName": file_name}, 30.0)


@server.tool()
async def list_saves(instance: str = "host") -> dict[str, Any]:
    """List and inspect saves in one isolated profile without changing the game."""
    return await asyncio.to_thread(_list_saves, instance)


@server.tool()
async def wait_for_state(instance: str, field: str, expected_json: str,
                         timeout_seconds: float = 60.0) -> dict[str, Any]:
    """Poll one live state field until it equals the supplied JSON value or timeout."""
    expected = json.loads(expected_json)
    deadline = time.monotonic() + max(0.5, min(timeout_seconds, 180.0))
    last_state: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        last_state = await get_state(instance)
        if _field(last_state, field) == expected:
            return {"matched": True, "field": field, "value": expected, "state": last_state}
        await asyncio.sleep(0.25)
    return {
        "matched": False,
        "field": field,
        "expected": expected,
        "actual": _field(last_state or {}, field),
        "state": last_state,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    server.run(transport="stdio")
