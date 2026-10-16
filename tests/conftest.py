"""One session for the whole run: the standalone server with the test world,
player A on client 1 (an administrator) and player B on client 2 (no rights).

Before each test the probe empties the hangar and puts the plugin config back
to the suite's base config, and player A moves to a fresh spot, so the tests
don't see each other's grids. Only test_restart.py restarts the server;
test_cluster.py replaces it with two cluster nodes and runs last.

HG_ATTACH=1 reuses a running server and clients (session.py start), HG_KEEP=1
leaves them running after the run.
"""

from __future__ import annotations

import math
import os
import re
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import session  # noqa: E402
import world  # noqa: E402
from session import CLIENTS, SERVER, Player  # noqa: E402

ATTACH = os.environ.get("HG_ATTACH") == "1"
KEEP = os.environ.get("HG_KEEP") == "1"

# No cooldown and no slot limit unless a test asks for them
BASE_CONFIG = {"SaveCooldownMinutes": 0, "MaxEntriesPerPlayer": 0}
# The test spots, a kilometer apart in empty space
SITE_SPACING_M = 1000.0
SITE_START = (20000.0, 0.0, 20000.0)


def wait_until(condition, timeout: float, what: str, interval: float = 0.5):
    deadline = time.monotonic() + timeout
    while not (result := condition()):
        if time.monotonic() > deadline:
            raise TimeoutError(f"Timed out waiting for {what}")
        time.sleep(interval)
    return result


def distance(p, q) -> float:
    return math.dist(p, q)


class Game:
    def __init__(self, server=SERVER):
        self.a, self.b = session.players()
        self.server = server
        self.site = SITE_START

    def configure(self, player: Player | None = None, **options) -> None:
        """Puts the suite's base config with these options into the running
        plugin, through the probe. The plugin saves it to Hangar.cfg."""
        values = {**BASE_CONFIG, **options}
        args = [f"{k}={v}" for k, v in values.items()]
        assert (player or self.a).probe("config", *args) == "Probe config: done"

    def new_site(self) -> tuple[float, float, float]:
        """Moves player A to a spot of its own and returns it"""
        # Counted in a file, so a run attached to a running server takes new spots
        counter = self.server.root / "sites"
        k = int(counter.read_text()) if counter.exists() else 0
        counter.write_text(str(k + 1))
        x, y, z = SITE_START
        self.site = (x + SITE_SPACING_M * k, y, z)
        self.go_to_site()
        return self.site

    def go_to_site(self) -> None:
        self.a.api.character_teleport(*self.site)
        wait_until(
            lambda: distance(self.a.position(), self.site) < 5, 20, "the teleport"
        )

    def at(self, dx: float = 0.0, dy: float = 0.0, dz: float = 0.0):
        x, y, z = self.site
        return (x + dx, y + dy, z + dz)

    def bring_b(self, position=None) -> None:
        """Teleports player B, who may not teleport himself, next to A"""
        position = position or self.at(5.0)
        reply = self.a.probe("teleport", self.b.steam_id, *position)
        assert reply == "Probe teleport: done", reply
        wait_until(
            lambda: distance(self.b.position(), position) < 5, 20, "B's teleport"
        )

    def paste(
        self,
        name: str,
        blocks: str | None = None,
        player: Player | None = None,
        offset=(0.0, 0.0, -30.0),
        static: bool = False,
    ) -> list[dict]:
        """Pastes a grid of these blocks (a ship by default), owned by the
        player (A by default), 30 m from the site along -z. Waits for the
        server to have it."""
        xml = world.blueprint(
            world.grid_xml(name, blocks or world.ship(), static=static)
        )
        pasted = (player or self.a).paste(xml, self.at(*offset))
        wait_until(lambda: self.grid(name)["count"], 15, f"the paste of {name}")
        return pasted

    def grid(self, name: str) -> dict:
        """The probe's view of the grids of this name on the server; count is
        0 when there is none"""
        reply = self.a.probe("grid", f'"{name}"').removeprefix("Probe grid: ")
        values = dict(p.split("=", 1) for p in reply.split())
        result: dict = {"count": int(values["count"])}
        if result["count"]:
            result |= {
                "id": int(values["id"]),
                "blocks": int(values["blocks"]),
                "static": values["static"] == "True",
                "pos": tuple(float(v) for v in values["pos"].split(",")),
                "group": int(values["group"]),
                "groupblocks": int(values["groupblocks"]),
                "owners": {
                    int(k): int(v)
                    for k, v in (o.split(":") for o in values["owners"].split(",") if o)
                },
                "pilots": int(values["pilots"]),
            }
        return result

    def items(self, name: str) -> dict[str, float]:
        reply = self.a.probe("items", f'"{name}"').removeprefix("Probe items: ")
        return {k: float(v) for k, v in (p.split("=") for p in reply.split())}

    def stored(self) -> str:
        """Hangar.cfg as the plugin last saved it"""
        path = self.server.plugin_cfg
        return path.read_text(encoding="utf-8") if path.exists() else ""

    def blobs(self, player: Player) -> list[Path]:
        folder = self.server.storage / "GridBlobs" / str(player.steam_id)
        return sorted(folder.glob("*.sbc")) if folder.exists() else []

    def restart(self) -> None:
        for client in CLIENTS:
            client.stop()
        self.server.stop()
        self.server.start()
        self.restart_clients()
        # Player A may come back as a new character at the spawn point, out of
        # the sync distance of the site, where his pastes would never show up
        self.go_to_site()

    def restart_clients(self) -> None:
        session.join_all()
        self.a, self.b = session.players()


@pytest.fixture(scope="session")
def game():
    attach = ATTACH and SERVER.pid() and all(c.pid() for c in CLIENTS)
    try:
        if not attach:
            session.stop_all()
            session.prepare()
            SERVER.start()
            session.join_all()
        yield Game()
    finally:
        if not KEEP:
            session.stop_all()


@pytest.fixture(autouse=True)
def fresh(request):
    """An empty hangar, the base config and a new spot for player A"""
    if request.module.__name__ == "test_cluster":
        yield
        return
    game = request.getfixturevalue("game")
    assert game.a.probe("wipe") == "Probe wipe: done"
    game.configure()
    game.new_site()
    yield


def pytest_collection_modifyitems(items):
    """The restart tests go last but one, the cluster tests last: a failed
    restart would take the session down with it"""
    order = {"test_restart": 1, "test_cluster": 2}
    items.sort(key=lambda item: order.get(item.module.__name__, 0))


SAVED = re.compile(r"Saved (.+) to hangar as ([0-9a-f]{8})\.")


def saved_id(reply: str) -> str:
    """The short entry id from a save reply"""
    match = SAVED.fullmatch(reply)
    assert match, reply
    return match.group(2)
