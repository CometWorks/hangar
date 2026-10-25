"""Admin safe zones and the BeforeLoad hook: with AdminSafeZonesOnly a grid is
saved only inside an admin safe zone and comes back only inside the one the
player stands in, back where it was if that is the zone it was saved in. The
probe makes the zones, spheres that allow everything, and sets a hook that
records what Hangar passes it."""

from __future__ import annotations

import pytest

import world
from conftest import distance, saved_id, wait_until

ZONE_RADIUS = 100.0
# Zone B's center, from the site; far enough that the zones don't touch
ZONE_B = (400.0, 0.0, 0.0)
OUTSIDE_SAVE = "You can only save grids inside a station's safe zone."
OUTSIDE_LOAD = "You can only load grids inside a station's safe zone."


@pytest.fixture(autouse=True)
def zones(game):
    yield
    assert game.a.probe("unzone") == "Probe unzone: done"
    assert game.a.probe("hook", "off") == "Probe hook: off"


def zone(game, name: str, center, radius: float = ZONE_RADIUS) -> None:
    assert game.a.probe("zone", f'"{name}"', *center, radius) == "Probe zone: done"


def teleport(game, position) -> None:
    game.a.api.character_teleport(*position)
    wait_until(lambda: distance(game.a.position(), position) < 5, 20, "the teleport")


def info(game) -> dict[str, str]:
    return dict(line.split(": ", 1) for line in game.a.hangar("info").splitlines())


def save_in_zone_a(game, name: str, blocks: str | None = None):
    """Pastes a ship, puts zone A around the site and saves the ship there;
    returns where it was"""
    game.configure(AdminSafeZonesOnly=True)
    game.paste(name, blocks)
    origin = game.grid(name)["pos"]
    zone(game, "Zone A", game.site)
    saved_id(game.a.hangar("save", name))
    return origin


def test_save_outside_a_zone_is_refused(game):
    game.configure(AdminSafeZonesOnly=True)
    game.paste("No zone save")
    assert game.a.hangar("save No zone save") == OUTSIDE_SAVE
    assert game.grid("No zone save")["count"] == 1
    assert game.a.hangar("list") == "Your hangar is empty."


def test_rule_off_saves_outside_zones(game):
    game.paste("Rule off")
    saved_id(game.a.hangar("save Rule off"))
    assert info(game)["zone"] == ""
    assert " in " not in game.a.hangar("list")


def test_same_zone_load_puts_the_grid_back(game):
    a = game.a
    origin = save_in_zone_a(game, "Zone home")
    [line] = a.hangar("list").splitlines()
    assert line.endswith(" in Zone A"), line
    assert info(game)["zone"] == "Zone A"

    # Out of the zone the load is refused and the entry stays
    teleport(game, game.at(*ZONE_B))
    assert a.hangar("load") == OUTSIDE_LOAD
    assert game.grid("Zone home")["count"] == 0
    assert len(a.hangar("list").splitlines()) == 1

    # Back in zone A, though 50 m from where it was saved, it comes back there
    teleport(game, game.at(50.0))
    assert a.hangar("load") == "Loaded Zone home from hangar."
    assert distance(game.grid("Zone home")["pos"], origin) < 0.5


def test_other_zone_load_lands_inside_that_zone(game):
    a = game.a
    save_in_zone_a(game, "Zone trip")
    zone(game, "Zone B", game.at(*ZONE_B))
    # The near-player spot, 150 m ahead, is outside zone B's 100 m: the grid is
    # pulled back into the zone
    teleport(game, game.at(*ZONE_B))
    a.api.character_look_at(*game.at(ZONE_B[0] + 500.0), tolerance=1)
    assert a.hangar("load") == "Loaded Zone trip from hangar."
    position = game.grid("Zone trip")["pos"]
    assert distance(position, game.at(*ZONE_B)) < ZONE_RADIUS, position


def test_zone_without_room_refuses_the_load(game):
    a = game.a
    # 25 m long, more than the game's smallest zone holds, 10 m in radius
    long_ship = world.armor(world.row(10)) + world.block(
        "BatteryBlock", "LargeBlockBatteryBlock", (0, 1, 0)
    )
    save_in_zone_a(game, "Zone tight", long_ship)
    zone(game, "Zone C", game.at(*ZONE_B), radius=10.0)
    teleport(game, game.at(*ZONE_B))
    reply = a.hangar("load")
    assert reply.startswith("No free space for Zone tight inside Zone C."), reply
    assert game.grid("Zone tight")["count"] == 0
    assert len(a.hangar("list").splitlines()) == 1


def test_before_load_hook_gets_the_zones_and_pcu(game):
    a = game.a
    save_in_zone_a(game, "Zone hook")
    pcu = info(game)["pcu"]
    zone(game, "Zone B", game.at(*ZONE_B))
    teleport(game, game.at(*ZONE_B))

    # A refusal is the player's reply, and the grid stays in the hangar
    assert a.probe("hook", "refuse") == "Probe hook: refuse"
    expected = f"saved=Zone A|here=Zone B|pcu={pcu}"
    assert a.hangar("load") == f"Probe refuses: {expected}"
    assert game.grid("Zone hook")["count"] == 0
    assert len(a.hangar("list").splitlines()) == 1

    assert a.probe("hook", "allow") == "Probe hook: allow"
    assert a.hangar("load") == "Loaded Zone hook from hangar."
    assert a.probe("hook", "last") == f"Probe hook: {expected}"


def test_before_load_hook_without_zones(game):
    a = game.a
    game.paste("Hook no zone")
    saved_id(a.hangar("save Hook no zone"))
    assert a.probe("hook", "allow") == "Probe hook: allow"
    assert a.hangar("load") == "Loaded Hook no zone from hangar."
    last = a.probe("hook", "last")
    assert last.startswith("Probe hook: saved=|here=|pcu="), last
