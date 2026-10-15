"""What may be saved, and how often: majority ownership, the slot, block and
PCU limits, the save cooldown, and the plugin switched off."""

from __future__ import annotations

import re

import world
from conftest import saved_id

MAJORITY = "You must own the majority of functional blocks on the main grid."


def test_majority_owner_may_save(game):
    a, b = game.a, game.b
    game.paste("Two thirds")
    a.probe("own", '"Two thirds"', b.identity_id, 1)
    assert saved_id(a.hangar("save Two thirds"))


def test_minority_owner_may_not_save(game):
    a, b = game.a, game.b
    game.paste("One third")
    a.probe("own", '"One third"', b.identity_id, 2)
    assert a.hangar("save One third") == MAJORITY
    assert game.grid("One third")["count"] == 1
    assert a.hangar("list") == "Your hangar is empty."


def test_other_players_grid(game):
    game.paste("Not yours")
    assert game.b.hangar("save Not yours") == MAJORITY
    assert game.grid("Not yours")["count"] == 1


def test_without_the_ownership_rule_anyone_may_save_any_grid(game):
    a, b = game.a, game.b
    game.configure(RequireMajorityOwnership=False)
    game.paste("Free for all")
    assert saved_id(b.hangar("save Free for all"))
    assert b.entries() == ["Free for all"]
    assert a.entries() == []
    assert b.hangar("load") == "Loaded Free for all from hangar."
    assert game.grid("Free for all")["owners"] == {b.identity_id: 3}


def test_armor_only_grid_can_not_be_saved(game):
    """With no terminal block the rule falls back to the grid's big owners,
    which armor never sets: even its builder can't store it"""
    game.paste("Armor only", world.armor(world.row(3)))
    assert game.a.hangar("save Armor only") == MAJORITY


def test_slot_limit(game):
    a, b = game.a, game.b
    game.configure(MaxEntriesPerPlayer=2)
    for name in ("Slot 1", "Slot 2", "Slot 3"):
        game.paste(name, offset=(0.0, 0.0, -30.0 - 15.0 * int(name[-1])))
    saved_id(a.hangar("save Slot 1"))
    saved_id(a.hangar("save Slot 2"))
    assert a.hangar("save Slot 3") == "Hangar is full. Limit: 2."
    assert game.grid("Slot 3")["count"] == 1

    # The limit is per player, and a load frees a slot
    game.bring_b()
    game.paste("B slot", player=b, offset=(0.0, 0.0, 30.0))
    assert saved_id(b.hangar("save B slot"))
    assert a.hangar("load 1") == "Loaded Slot 1 from hangar."
    assert saved_id(a.hangar("save Slot 3"))


def test_block_limit(game):
    a = game.a
    game.configure(MaxBlocksPerEntry=world.SHIP_BLOCKS - 1)
    game.paste("Too many blocks")
    assert a.hangar("save Too many blocks") == (
        f"Grid is too large: {world.SHIP_BLOCKS} blocks, limit {world.SHIP_BLOCKS - 1}."
    )
    game.configure(MaxBlocksPerEntry=world.SHIP_BLOCKS)
    assert saved_id(a.hangar("save Too many blocks"))


def test_block_limit_counts_connected_grids(game):
    a = game.a
    game.paste("Rotor limit", world.rotor_base())
    a.probe("attach", '"Rotor limit"')
    blocks = game.grid("Rotor limit")["groupblocks"]
    game.configure(MaxBlocksPerEntry=blocks - 1)
    assert a.hangar("save Rotor limit") == (
        f"Grid is too large: {blocks} blocks, limit {blocks - 1}."
    )


def test_pcu_limit(game):
    a = game.a
    game.configure(MaxPcuPerEntry=1)
    game.paste("Too much PCU")
    reply = a.hangar("save Too much PCU")
    match = re.fullmatch(r"Grid is too expensive: (\d+) PCU, limit 1\.", reply)
    assert match, reply
    pcu = int(match.group(1))
    assert pcu > 1
    game.configure(MaxPcuPerEntry=pcu)
    assert saved_id(a.hangar("save Too much PCU"))
    assert f", {pcu} PCU, " in a.hangar("list")


def test_save_cooldown(game):
    a, b = game.a, game.b
    game.configure(SaveCooldownMinutes=60)
    game.paste("Cool 1")
    game.paste("Cool 2", offset=(0.0, 0.0, -50.0))
    # A failed save doesn't start the cooldown
    assert a.hangar("save No such grid") == "Grid not found: No such grid"
    saved_id(a.hangar("save Cool 1"))
    assert a.hangar("save Cool 2") == "Save cooldown active for 60 more minutes."

    # It is per player
    game.bring_b()
    game.paste("Cool B", player=b, offset=(0.0, 0.0, 30.0))
    assert saved_id(b.hangar("save Cool B"))

    assert a.probe("cooldown", a.steam_id, 45) == "Probe cooldown: done"
    assert a.hangar("save Cool 2") == "Save cooldown active for 15 more minutes."
    assert a.probe("cooldown", a.steam_id, 61) == "Probe cooldown: done"
    assert saved_id(a.hangar("save Cool 2"))


def test_loading_does_not_wait_for_the_cooldown(game):
    a = game.a
    game.configure(SaveCooldownMinutes=60)
    game.paste("Cool load")
    saved_id(a.hangar("save Cool load"))
    assert a.hangar("load") == "Loaded Cool load from hangar."


def test_disabled_plugin_neither_saves_nor_loads(game):
    a = game.a
    game.paste("Before off")
    saved_id(a.hangar("save Before off"))
    game.paste("While off", offset=(0.0, 0.0, -50.0))

    assert a.hangar("disable") == "Hangar disabled."
    assert a.hangar("save While off") == "Hangar is disabled."
    assert a.hangar("load") == "Hangar is disabled."
    assert game.grid("While off")["count"] == 1
    assert "enabled: False" in a.hangar("status").splitlines()
    # Listing still works, for players to see what they have
    assert a.entries() == ["Before off"]
    assert "<Enabled>false</Enabled>" in game.stored()

    assert a.hangar("enable") == "Hangar enabled."
    assert a.hangar("load") == "Loaded Before off from hangar."
