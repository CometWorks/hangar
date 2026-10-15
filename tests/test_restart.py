"""What survives a server restart: the stored grids of both players, the
cooldowns, and the config the commands and the options changed. And a broken
config file, which keeps Hangar from loading rather than losing the index of
stored grids.

These tests restart the server and join the clients again; conftest.py runs
them after the others.
"""

from __future__ import annotations

import re

import pytest

from conftest import distance, saved_id


def test_hangar_and_config_survive_a_restart(game):
    a, b = game.a, game.b
    game.configure(
        SaveCooldownMinutes=60,
        MaxEntriesPerPlayer=5,
        LoadOffsetMeters=77,
        DefaultLoadMode="OriginalPosition",
    )
    game.paste("Kept 1")
    game.paste("Kept 2", offset=(0.0, 0.0, -50.0))
    origin = game.grid("Kept 1")["pos"]
    saved_id(a.hangar("save Kept 1"))
    a.probe("cooldown", a.steam_id, 61)
    saved_id(a.hangar("save Kept 2"))
    game.bring_b()
    game.paste("Kept B", player=b, offset=(0.0, 0.0, 30.0))
    saved_id(b.hangar("save Kept B"))
    assert a.hangar("disable") == "Hangar disabled."

    game.restart()
    a, b = game.a, game.b
    stored = game.stored()
    for element in (
        "<Enabled>false</Enabled>",
        "<SaveCooldownMinutes>60</SaveCooldownMinutes>",
        "<MaxEntriesPerPlayer>5</MaxEntriesPerPlayer>",
        "<LoadOffsetMeters>77</LoadOffsetMeters>",
        "<DefaultLoadMode>OriginalPosition</DefaultLoadMode>",
    ):
        assert element in stored, element
    assert a.hangar("status").splitlines()[0] == "enabled: False"
    assert a.hangar("enable") == "Hangar enabled."

    assert a.entries() == ["Kept 1", "Kept 2"]
    assert b.entries() == ["Kept B"]
    game.paste("After restart", offset=(0.0, 0.0, -80.0))
    # The restart took some of the hour
    reply = a.hangar("save After restart")
    match = re.fullmatch(r"Save cooldown active for (\d+) more minutes\.", reply)
    assert match and 30 < int(match.group(1)) <= 60, reply
    assert a.hangar("load 1") == "Loaded Kept 1 from hangar."
    assert distance(game.grid("Kept 1")["pos"], origin) < 0.5
    assert b.hangar("load") == "Loaded Kept B from hangar."


def test_broken_config_keeps_hangar_unloaded(game):
    """Hangar.cfg holds the standalone index of stored grids, so Hangar must
    not start over with an empty one. It fails to load instead and leaves the
    file as it is."""
    game.paste("Before the break")
    saved_id(game.a.hangar("save Before the break"))
    for client in (game.a.client, game.b.client):
        client.stop()
    game.server.stop()
    broken = game.stored().replace("</PluginConfig>", "")
    game.server.plugin_cfg.write_text(broken, encoding="utf-8")
    game.server.start()
    game.restart_clients()

    assert game.stored() == broken
    with pytest.raises(TimeoutError):
        game.a.command("!hangar list", timeout=5)
    assert "Failed to initialize Hangar" in game.server.loader_log()
