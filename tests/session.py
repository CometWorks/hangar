"""A running test session: the server with its world, and the players on the
clients. Also usable from the command line while iterating::

    uv run python tests/session.py start     # server 0 and clients A and B
    uv run python tests/session.py stop
"""

from __future__ import annotations

import shutil
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import rig  # noqa: E402
import world  # noqa: E402

SERVER = rig.Server(0)
CLIENT_A = rig.Client(1, "Alpha")
CLIENT_B = rig.Client(2, "Bravo")
CLIENTS = (CLIENT_A, CLIENT_B)

# The cluster nodes of test_cluster.py, and a second client of player A, so A
# can be on both nodes at once
NODES = (rig.Server(1, "node-1"), rig.Server(2, "node-2"))
CLIENT_A2 = rig.Client(3, "Alpha", CLIENT_A.steam_id)

REPLY_TIMEOUT_S = 20.0
# A reply is complete once no more of it came for this long
QUIET_S = 1.0
# Command replies come from the command's root, errors and Hangar's own chat
# messages from its title
ROOT_TITLES = {"hangar": "Hangar", "h": "Hangar", "hgprobe": "HangarProbe"}


class Player:
    """A client's player: chat commands, pastes and its character through
    Remote"""

    def __init__(self, client: rig.Client, identity_id: int):
        self.client = client
        self.identity_id = identity_id
        self.steam_id = client.steam_id
        self.api = client.api()

    # --- chat ----------------------------------------------------------------

    def _chat(self) -> list[tuple[str, str, str]]:
        hud = self.api.get_chat_messages().get("hud") or []
        return [(m.get("sender"), m.get("text"), m.get("timestamp")) for m in hud]

    def mark(self) -> set:
        return set(self._chat())

    def chat_since(self, before: set) -> list[tuple[str, str]]:
        return [(s, t) for s, t, ts in self._chat() if (s, t, ts) not in before]

    def command(self, text: str, timeout: float = REPLY_TIMEOUT_S) -> str:
        """Sends a chat command and returns its reply. A reply of several
        messages comes back as their lines."""
        prefix = text.split()[0][1:]
        senders = {prefix, ROOT_TITLES.get(prefix, "Hangar")}
        before = self.mark()
        self.api.send_chat(text)
        deadline = time.monotonic() + timeout
        replies, last = [], None
        while time.monotonic() < deadline:
            new = [t for s, t in self.chat_since(before) if s in senders]
            if new != replies:
                replies, last = new, time.monotonic()
            elif replies and time.monotonic() - last > QUIET_S:
                return "\n".join(replies)
            time.sleep(0.3)
        if replies:
            return "\n".join(replies)
        raise TimeoutError(f"No reply to {text!r}")

    def hangar(self, *args) -> str:
        return self.command(" ".join(["!hangar", *map(str, args)]))

    def probe(self, *args) -> str:
        return self.command(" ".join(["!hgprobe", *map(str, args)]))

    def entry_id(self, number: int = 1) -> str:
        """The full id of an entry"""
        return self.hangar("info", number).splitlines()[0].removeprefix("id: ")

    def entries(self) -> list[str]:
        """The grid names of the player's hangar list, in its order"""
        reply = self.hangar("list")
        if reply == "Your hangar is empty.":
            return []
        return [
            line.split(". ", 1)[1].rsplit(" [", 1)[0] for line in reply.splitlines()
        ]

    # --- world ---------------------------------------------------------------

    def paste(self, xml: str, position, timeout: float = 15.0) -> list[dict]:
        """Pastes a blueprint, owned by this player, with its first grid's
        origin at the position. Returns the pasted grids."""
        return self.api.paste_blueprint(
            xml=xml,
            position=position,
            forward=(0, 0, -1),
            up=(0, 1, 0),
            own=True,
            timeout_seconds=timeout,
        )

    def position(self) -> tuple[float, float, float]:
        return tuple(self.api.get_character()["position"])

    def sees(self, name: str) -> bool:
        """Whether this client has a grid of this name"""
        return any(g.get("name") == name for g in self.api.list_grids())


# ---------------------------------------------------------------------------
# Start and stop
# ---------------------------------------------------------------------------


def players_by_steam_id() -> dict[int, tuple[int, str]]:
    return {
        CLIENT_A.steam_id: (world.PLAYER_A, CLIENT_A.name),
        CLIENT_B.steam_id: (world.PLAYER_B, CLIENT_B.name),
    }


def prepare(server: rig.Server = SERVER) -> None:
    """A fresh world and the server's configs. The server starts with no
    Hangar config and no stored grids."""
    world.prepare(rig.EMPTY_WORLD, server.world, players_by_steam_id())
    server.prepare([CLIENT_A.steam_id])
    server.plugin_cfg.unlink(missing_ok=True)
    (server.root / "sites").unlink(missing_ok=True)
    if server.data.joinpath("Hangar").exists():
        shutil.rmtree(server.data / "Hangar")


def join(client: rig.Client, server: rig.Server = SERVER) -> rig.RemoteAPI:
    """Launches a client, which connects on its own, and waits for its
    character"""
    client.launch(server.port)
    api = client.wait_api()
    rig.wait_world(api)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    return api


def join_all(pairs=None) -> None:
    """Joins clients to servers in parallel, (client, server) pairs"""
    pairs = pairs or [(c, SERVER) for c in CLIENTS]
    errors = []

    def run(client, server):
        try:
            join(client, server)
        except Exception as error:  # noqa: BLE001 -- reported below
            errors.append((client.name, error))

    threads = [threading.Thread(target=run, args=p) for p in pairs]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    if errors:
        raise RuntimeError(f"Joining failed: {errors}")


def stop_all() -> None:
    for client in (*CLIENTS, CLIENT_A2):
        client.stop()
    for server in (SERVER, *NODES):
        server.stop()


def players() -> tuple[Player, Player]:
    return Player(CLIENT_A, world.PLAYER_A), Player(CLIENT_B, world.PLAYER_B)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_all()
        prepare()
        SERVER.start()
        join_all()
        print(
            f"Joined. Remote ports {CLIENT_A.port}, {CLIENT_B.port}; server log {SERVER.log}"
        )
    elif command == "stop":
        stop_all()
    else:
        sys.exit(__doc__)
