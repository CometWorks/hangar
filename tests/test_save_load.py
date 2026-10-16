"""Saving a grid into the hangar and loading it back: what is stored, what
comes back, where it comes back, and what the save options change. Player A
saves his own ships unless a test says otherwise."""

from __future__ import annotations

import pytest

import world
from conftest import distance, saved_id, wait_until


def test_save_by_name_stores_the_grid_and_removes_it(game):
    a = game.a
    game.paste("Save by name")
    wait_until(lambda: a.sees("Save by name"), 10, "the ship on the client")

    short_id = saved_id(a.hangar("save Save by name"))
    assert game.grid("Save by name")["count"] == 0
    wait_until(lambda: not a.sees("Save by name"), 10, "the ship to go on the client")

    [blob] = game.blobs(a)
    assert blob.stem.startswith(short_id)
    assert "<DisplayName>Save by name</DisplayName>" in blob.read_text(encoding="utf-8")
    assert short_id in game.stored()

    [line] = a.hangar("list").splitlines()
    assert line.startswith(
        f"1. Save by name [{short_id}] 1 grids, {world.SHIP_BLOCKS} blocks, "
    )


def test_load_brings_back_blocks_owner_and_inventory(game):
    a = game.a
    game.paste("Cargo ship")
    assert (
        a.probe("fill", '"Cargo ship"', "SteelPlate", 150)
        == "Probe fill: 150 SteelPlate"
    )
    assert game.items("Cargo ship") == {"SteelPlate": 150}
    saved_id(a.hangar("save Cargo ship"))

    assert a.hangar("load") == "Loaded Cargo ship from hangar."
    grid = game.grid("Cargo ship")
    assert grid["count"] == 1
    assert grid["blocks"] == world.SHIP_BLOCKS
    assert grid["owners"] == {a.identity_id: 3}
    assert not grid["static"]
    assert game.items("Cargo ship") == {"SteelPlate": 150}
    wait_until(lambda: a.sees("Cargo ship"), 15, "the ship on the client")

    # Loading consumes the entry and its blob
    assert a.hangar("list") == "Your hangar is empty."
    assert game.blobs(a) == []


def test_near_player_load_puts_the_grid_in_front_of_the_player(game):
    a = game.a
    game.configure(LoadOffsetMeters=60)
    game.paste("Near load")
    saved_id(a.hangar("save Near load"))

    character = a.api.get_character()
    head = character["position"]
    forward = character["forward"]
    assert a.hangar("load") == "Loaded Near load from hangar."
    expected = [h + 60 * f for h, f in zip(head, forward)]
    # The head is above the character's position; the grid's origin is its
    # first block
    assert distance(game.grid("Near load")["pos"], expected) < 5


def test_original_position_load(game):
    a = game.a
    game.configure(DefaultLoadMode="OriginalPosition")
    game.paste("Original spot")
    origin = game.grid("Original spot")["pos"]
    saved_id(a.hangar("save Original spot"))
    info = dict(line.split(": ", 1) for line in a.hangar("info").splitlines())
    stored = tuple(float(v) for v in info["original_position"].split(", "))
    assert distance(stored, origin) < 0.01

    # Load from 200 m away: the grid goes back where it was
    a.api.character_teleport(*game.at(200.0))
    wait_until(lambda: distance(a.position(), game.at(200.0)) < 5, 20, "the teleport")
    assert a.hangar("load") == "Loaded Original spot from hangar."
    assert distance(game.grid("Original spot")["pos"], origin) < 0.5


def test_forced_near_player_load_in_original_position_mode(game):
    a = game.a
    game.configure(DefaultLoadMode="OriginalPosition", LoadOffsetMeters=40)
    game.paste("Forced near")
    origin = game.grid("Forced near")["pos"]
    saved_id(a.hangar("save Forced near"))

    a.api.character_teleport(*game.at(300.0))
    wait_until(lambda: distance(a.position(), game.at(300.0)) < 5, 20, "the teleport")
    assert a.hangar("load 1 true") == "Loaded Forced near from hangar."
    position = game.grid("Forced near")["pos"]
    assert distance(position, a.position()) < 45
    assert distance(position, origin) > 200


def test_save_the_grid_in_sight(game):
    a = game.a
    game.paste("In sight", offset=(0.0, 0.0, -20.0))
    a.api.character_look_at(*game.at(2.5, 0.0, -20.0), tolerance=1)
    assert saved_id(a.hangar("save"))
    assert game.grid("In sight")["count"] == 0


def test_save_with_nothing_in_sight(game):
    a = game.a
    game.paste("Out of sight", offset=(0.0, 0.0, -20.0))
    a.api.character_look_at(*game.at(0.0, 0.0, 100.0), tolerance=1)
    assert a.hangar("save") == "No grid found in your line of sight."
    assert game.grid("Out of sight")["count"] == 1


def test_static_grid_comes_back_static(game):
    a = game.a
    game.paste("Station", static=True)
    assert game.grid("Station")["static"]
    saved_id(a.hangar("save Station"))
    assert "kind: Static" in a.hangar("info").splitlines()
    assert a.hangar("load") == "Loaded Station from hangar."
    assert game.grid("Station")["static"]


def test_keeping_the_original_on_save(game):
    """RemoveOriginalOnSave off stores a copy and leaves the grid"""
    a = game.a
    game.configure(RemoveOriginalOnSave=False)
    game.paste("Kept")
    saved_id(a.hangar("save Kept"))
    assert game.grid("Kept")["count"] == 1
    assert a.hangar("load") == "Loaded Kept from hangar."
    assert game.grid("Kept")["count"] == 2


def test_ownership_moves_to_the_loader(game):
    a, b = game.a, game.b
    game.paste("Shared ship")
    assert a.probe("own", '"Shared ship"', b.identity_id, 1) == "Probe own: 1"
    assert game.grid("Shared ship")["owners"] == {a.identity_id: 2, b.identity_id: 1}
    saved_id(a.hangar("save Shared ship"))
    assert a.hangar("load") == "Loaded Shared ship from hangar."
    assert game.grid("Shared ship")["owners"] == {a.identity_id: 3}


def test_ownership_kept_without_transfer(game):
    a, b = game.a, game.b
    game.configure(TransferOwnershipOnLoad=False)
    game.paste("Mixed ship")
    a.probe("own", '"Mixed ship"', b.identity_id, 1)
    saved_id(a.hangar("save Mixed ship"))
    assert a.hangar("load") == "Loaded Mixed ship from hangar."
    assert game.grid("Mixed ship")["owners"] == {a.identity_id: 2, b.identity_id: 1}


def test_rotor_head_is_saved_and_loaded_with_its_base(game):
    a = game.a
    game.paste("Rotor base", world.rotor_base())
    reply = a.probe("attach", '"Rotor base"')
    assert reply != "Probe attach: top=0", reply
    wait_until(lambda: game.grid("Rotor base")["group"] == 2, 10, "the rotor head")
    blocks = game.grid("Rotor base")["groupblocks"]

    saved_id(a.hangar("save Rotor base"))
    assert "grids: 2 total" in a.hangar("info")
    assert a.hangar("load") == "Loaded Rotor base from hangar."
    grid = game.grid("Rotor base")
    assert (grid["group"], grid["groupblocks"]) == (2, blocks)


def dock(game, name: str) -> None:
    """A static dock with a ship locked to its connector. The game refuses a
    paste touching another grid, so the ship comes a cell away and the probe
    moves it in."""
    game.paste(name, world.dock(), static=True)
    dx, dy, dz = world.DOCKED_OFFSET
    game.paste(name + " ship", world.docked_ship(), offset=(dx + 2.5, dy, dz - 30.0))
    assert game.a.probe("move", f'"{name} ship"', -2.5, 0, 0).startswith(
        "Probe move: pos="
    )
    reply = game.a.probe("connect", f'"{name}"')
    assert reply == "Probe connect: status=Connected", reply
    assert game.grid(name)["group"] == 2


def test_docked_ship_is_saved_with_its_dock(game):
    dock(game, "Dock")
    saved_id(game.a.hangar("save Dock"))
    assert game.grid("Dock")["count"] == 0
    assert game.grid("Dock ship")["count"] == 0
    assert "grids: 2 total, 1 static, 1 large, 0 small" in game.a.hangar("info")


def test_docked_ship_stays_without_connected_grids(game):
    """IncludeConnectedGrids off saves the mechanical group: a rotor head
    still goes along, a ship on a connector doesn't"""
    game.configure(IncludeConnectedGrids=False)
    dock(game, "Lone dock")
    saved_id(game.a.hangar("save Lone dock"))
    assert game.grid("Lone dock")["count"] == 0
    assert game.grid("Lone dock ship")["count"] == 1
    assert "grids: 1 total" in game.a.hangar("info")


def test_pilot_is_left_out(game):
    """Player A saves his ship from its cockpit: he gets out, and the stored
    ship has no pilot"""
    a = game.a
    game.paste("Piloted")
    assert a.probe("seat", '"Piloted"', a.identity_id) == "Probe seat: requested"
    wait_until(lambda: game.grid("Piloted")["pilots"] == 1, 10, "A in the cockpit")
    wait_until(lambda: a.api.get_character().get("state") == "sitting", 10, "A seated")

    saved_id(a.hangar("save Piloted"))
    wait_until(lambda: a.api.get_character().get("state") != "sitting", 15, "A out")
    assert a.api.get_character()["health"] > 0
    assert a.hangar("load") == "Loaded Piloted from hangar."
    grid = game.grid("Piloted")
    assert (grid["blocks"], grid["pilots"]) == (world.SHIP_BLOCKS, 0)


@pytest.mark.xfail(strict=True, reason="SE1-0240: a load does not look for free space")
def test_load_does_not_overlap_another_grid(game):
    """Something else took the spot since the save: the load must not put the
    grid inside it. Both are stations, so physics doesn't push them apart."""
    a = game.a
    game.configure(DefaultLoadMode="OriginalPosition")
    game.paste("Spot keeper", static=True)
    origin = game.grid("Spot keeper")["pos"]
    saved_id(a.hangar("save Spot keeper"))
    game.paste("Squatter", static=True)
    assert distance(game.grid("Squatter")["pos"], origin) < 0.5

    reply = a.hangar("load")
    if reply == "Loaded Spot keeper from hangar.":
        assert distance(game.grid("Spot keeper")["pos"], origin) > 10
