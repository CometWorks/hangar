"""Hangar on two cluster nodes that share one storage (hangar#1): a player's
entries, slot limit and cooldown are the same on every node, and the state
lives in the shared storage, not in the nodes' configs.

The nodes are two Magnetar servers started with CLUSTER_NODE_ID and one
CLUSTER_SHARED_ROOT, which is what makes PluginSdk, and so Hangar, treat them
as cluster processes. There is no gateway or World Authority: each node has a
world of its own. Player A is on both at once, on clients 1 and 3. These tests
replace the standalone server and run last.
"""

from __future__ import annotations

import itertools
import os
import shutil

import pytest

import rig
import session
import world
from conftest import KEEP, distance, saved_id, wait_until
from session import CLIENT_A, CLIENT_A2, NODES, Player

SITE = (20000.0, 0.0, -20000.0)

if os.environ.get("HG_SKIP_CLUSTER") == "1":
    pytestmark = pytest.mark.skip("HG_SKIP_CLUSTER=1")


class Cluster:
    def __init__(self):
        self.a1 = Player(CLIENT_A, world.PLAYER_A)
        self.a2 = Player(CLIENT_A2, world.PLAYER_A)
        self.pasted = itertools.count()

    def configure(self, **options) -> None:
        values = {"SaveCooldownMinutes": 0, "MaxEntriesPerPlayer": 0, **options}
        for player in (self.a1, self.a2):
            reply = player.probe("config", *(f"{k}={v}" for k, v in values.items()))
            assert reply == "Probe config: done", reply

    def grid(self, player: Player, name: str) -> int:
        """How many grids of this name the player's node has"""
        reply = player.probe("grid", f'"{name}"')
        return int(reply.removeprefix("Probe grid: count=").split()[0])

    def paste(self, player: Player, name: str) -> None:
        xml = world.blueprint(world.grid_xml(name, world.ship()))
        x, y, z = SITE
        player.paste(xml, (x, y, z - 30.0 - 15.0 * next(self.pasted)))
        wait_until(lambda: self.grid(player, name), 15, f"the paste of {name}")

    @property
    def state(self):
        return NODES[0].storage / "Players" / f"{CLIENT_A.steam_id}.xml"


@pytest.fixture(scope="module")
def cluster():
    session.stop_all()
    if rig.SHARED.exists():
        shutil.rmtree(rig.SHARED)
    try:
        for node in NODES:
            session.prepare(node)
            node.start()
        session.join_all([(CLIENT_A, NODES[0]), (CLIENT_A2, NODES[1])])
        yield Cluster()
    finally:
        if not KEEP:
            session.stop_all()


@pytest.fixture(autouse=True)
def empty(cluster):
    assert cluster.a1.probe("wipe") == "Probe wipe: done"
    cluster.configure()
    for player in (cluster.a1, cluster.a2):
        player.api.character_teleport(*SITE)
        wait_until(lambda: distance(player.position(), SITE) < 5, 20, "the teleport")


def test_entry_saved_on_one_node_loads_on_the_other(cluster):
    a1, a2 = cluster.a1, cluster.a2
    cluster.paste(a1, "Traveller")
    short_id = saved_id(a1.hangar("save Traveller"))
    assert cluster.grid(a1, "Traveller") == 0

    assert a2.entries() == ["Traveller"]
    assert a2.hangar("info").splitlines()[0].startswith(f"id: {short_id}")
    assert a2.hangar("load") == "Loaded Traveller from hangar."
    assert cluster.grid(a2, "Traveller") == 1
    assert a1.hangar("list") == "Your hangar is empty."
    assert list((NODES[0].storage / "GridBlobs").rglob("*.sbc")) == []


def test_state_is_in_the_shared_storage(cluster):
    a1 = cluster.a1
    cluster.paste(a1, "Shared state")
    short_id = saved_id(a1.hangar("save Shared state"))

    assert short_id in cluster.state.read_text(encoding="utf-8")
    [blob] = (NODES[0].storage / "GridBlobs" / str(CLIENT_A.steam_id)).glob("*.sbc")
    assert blob.stem.startswith(short_id)
    for node in NODES:
        config = (
            node.plugin_cfg.read_text(encoding="utf-8")
            if node.plugin_cfg.exists()
            else ""
        )
        assert short_id not in config
    status = dict(line.split(": ", 1) for line in a1.hangar("status").splitlines())
    assert status["storage_root"] == str(NODES[0].storage)
    assert (status["entries"], status["cooldowns"]) == ("1", "1")


def test_slot_limit_counts_both_nodes(cluster):
    a1, a2 = cluster.a1, cluster.a2
    cluster.configure(MaxEntriesPerPlayer=2)
    cluster.paste(a1, "Node 1 first")
    cluster.paste(a1, "Node 1 second")
    cluster.paste(a2, "Node 2 first")
    saved_id(a1.hangar("save Node 1 first"))
    saved_id(a2.hangar("save Node 2 first"))
    assert a1.hangar("save Node 1 second") == "Hangar is full. Limit: 2."
    assert a1.entries() == a2.entries() == ["Node 1 first", "Node 2 first"]


def test_cooldown_counts_both_nodes(cluster):
    a1, a2 = cluster.a1, cluster.a2
    cluster.configure(SaveCooldownMinutes=60)
    cluster.paste(a1, "Cool node 1")
    cluster.paste(a2, "Cool node 2")
    saved_id(a1.hangar("save Cool node 1"))
    assert a2.hangar("save Cool node 2") == "Save cooldown active for 60 more minutes."
    assert a2.probe("cooldown", CLIENT_A.steam_id, 61) == "Probe cooldown: done"
    assert saved_id(a2.hangar("save Cool node 2"))


def test_remove_on_the_other_node(cluster):
    a1, a2 = cluster.a1, cluster.a2
    cluster.paste(a1, "Removed elsewhere")
    saved_id(a1.hangar("save Removed elsewhere"))
    entry_id = a2.entry_id()
    assert a2.hangar("remove", entry_id) == "Removed Removed elsewhere from hangar."
    assert a1.hangar("list") == "Your hangar is empty."
    assert a1.hangar("load", entry_id) == "Your hangar is empty."


# A standalone Hangar.cfg with one stored entry and a cooldown
LEGACY_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<PluginConfig>
  <HangarEntries>
    <HangarEntry>
      <Id>0123456789abcdef0123456789abcdef</Id>
      <Scope>Player</Scope>
      <OwnerId>{steam_id}</OwnerId>
      <OwnerName>Alpha</OwnerName>
      <GridName>Legacy</GridName>
      <BlobPath>GridBlobs/{steam_id}/0123456789abcdef0123456789abcdef.sbc</BlobPath>
    </HangarEntry>
  </HangarEntries>
</PluginConfig>
"""


def test_legacy_entries_keep_hangar_off_a_node(cluster):
    """A node whose own config still has standalone entries refuses to start
    Hangar, rather than hiding them"""
    node = NODES[1]
    CLIENT_A2.stop()
    node.stop()
    node.plugin_cfg.write_text(LEGACY_CONFIG.format(steam_id=CLIENT_A.steam_id))
    node.start()
    session.join_all([(CLIENT_A2, node)])

    assert Player(CLIENT_A2, world.PLAYER_A).hangar("list") == "Hangar is not loaded."
    assert "must be migrated" in node.loader_log()
    # The other node is unaffected
    assert cluster.a1.hangar("list") == "Your hangar is empty."
