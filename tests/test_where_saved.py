"""The options that bring a grid back only where it was saved: the original
position, no choice for players and, when its bounding sphere would meet another
grid's there, the nearest spot clear of them. A long row of armor stands for a
station with a shipyard: its sphere is big and mostly empty, so a ship can be in
it without touching it. Without the options all stays as it was."""

from __future__ import annotations

import pytest

import world
from conftest import distance, saved_id, wait_until
from test_zones import STORED, info, store, teleport, zone

WHERE_SAVED = {
    "DefaultLoadMode": "OriginalPosition",
    "PlayersMayChooseLoadPlace": False,
    "LoadClearOfGridSpheres": True,
}
STATION_BLOCKS = 61
# From the ship to the row's middle: clear of the ship, well inside the sphere
BESIDE = 20.0


@pytest.fixture(autouse=True)
def zones(game):
    yield
    assert game.a.probe("unzone") == "Probe unzone: done"
    assert game.a.probe("hook", "off") == "Probe hook: off"


def station_beside(game, ship_center) -> dict:
    """A long static grid whose sphere holds ship_center and whose blocks don't
    come near it. Pasted out of the way, as a paste may not touch anything."""
    game.paste(
        "Long row",
        world.armor(world.row(STATION_BLOCKS)),
        static=True,
        offset=(0.0, 400.0, 0.0),
    )
    center = game.grid("Long row")["center"]
    delta = [
        ship_center[0] - center[0],
        ship_center[1] + BESIDE - center[1],
        ship_center[2] - center[2],
    ]
    reply = game.a.probe("move", '"Long row"', *delta)
    assert reply.startswith("Probe move: pos="), reply
    grid = game.grid("Long row")
    assert distance(grid["center"], ship_center) < BESIDE + 1
    return grid


def assert_nearest_clear(ship: dict, other: dict) -> None:
    gap = distance(ship["center"], other["center"]) - ship["radius"] - other["radius"]
    assert 0 < gap < 1.5, gap


def test_comes_back_where_saved_however_far_the_player_is(game):
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("Far spot")
    origin = game.grid("Far spot")["pos"]
    saved_id(a.hangar("save Far spot"))

    teleport(game, game.at(600.0))
    assert a.hangar("load") == "Loaded Far spot from hangar."
    assert distance(game.grid("Far spot")["pos"], origin) < 0.5


def test_players_can_not_choose_when_the_option_is_off(game):
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("No near")
    saved_id(a.hangar("save No near"))

    reply = a.hangar("load 1 true")
    assert reply == "On this server players can't choose where a grid comes back."
    assert game.grid("No near")["count"] == 0
    assert a.entries() == ["No near"]


def test_a_grid_nearby_that_its_sphere_does_not_meet_changes_nothing(game):
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("Keeps spot")
    ship = game.grid("Keeps spot")
    saved_id(a.hangar("save Keeps spot"))
    game.paste("Neighbor", offset=(0.0, 0.0, -60.0))
    neighbor = game.grid("Neighbor")
    gap = distance(ship["center"], neighbor["center"])
    assert gap > ship["radius"] + neighbor["radius"]

    assert a.hangar("load") == "Loaded Keeps spot from hangar."
    assert distance(game.grid("Keeps spot")["pos"], ship["pos"]) < 0.5


def test_a_sphere_that_meets_moves_it_to_the_nearest_clear_spot(game):
    """No block of the row is near it: only the spheres meet, which the game's
    own check would let through"""
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("In the sphere")
    ship = game.grid("In the sphere")
    saved_id(a.hangar("save In the sphere"))
    station = station_beside(game, ship["center"])

    reply = a.hangar("load")
    assert reply.startswith("Loaded In the sphere from hangar, "), reply
    assert reply.endswith(" m from where it was saved as that spot is taken."), reply
    back = game.grid("In the sphere")
    assert_nearest_clear(back, station)
    moved = distance(back["pos"], ship["pos"])
    assert moved == pytest.approx(int(reply.split(", ")[1].split(" ")[0]), abs=1)


def test_a_ship_stored_from_a_shipyard_comes_back_outside_the_station(game):
    """As the shipyard's deadline stores it (SE-929): inside the station's sphere
    and its zone. It stays in the zone, and it hasn't changed zones for a fee."""
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("From the yard")
    ship = game.grid("From the yard")
    station = station_beside(game, ship["center"])
    zone(game, "Yard zone", station["center"], 400.0)
    assert store(game, "From the yard", ship["center"]) == STORED
    assert info(game)["zone"] == "Yard zone"
    assert a.probe("hook", "allow") == "Probe hook: allow"

    assert a.hangar("load").startswith("Loaded From the yard from hangar, ")
    assert_nearest_clear(game.grid("From the yard"), station)
    last = a.probe("hook", "last")
    assert last.startswith("Probe hook: saved=Yard zone|here=Yard zone|pcu="), last


def test_a_clear_spot_is_not_in_a_safe_zone_it_was_not_in(game):
    """The zone covers the side of the sphere a free spot would be nearest on"""
    a = game.a
    game.configure(**WHERE_SAVED)
    game.paste("Outside zones")
    ship = game.grid("Outside zones")
    saved_id(a.hangar("save Outside zones"))
    station = station_beside(game, ship["center"])
    x, y, z = ship["center"]
    zone_center = (x, y - station["radius"], z)
    zone(game, "Not here", zone_center, 40.0)

    assert a.hangar("load").startswith("Loaded Outside zones from hangar, ")
    back = game.grid("Outside zones")
    assert_nearest_clear(back, station)
    assert distance(back["center"], zone_center) > 40.0 + back["radius"]


def test_the_search_ends_and_clears_the_spheres_at_any_coordinates(game):
    """Rounding at a sphere's edge once kept it going and hung the game thread"""
    assert game.a.probe("clearspots", 2000) == "Probe clearspots: cases=2000 bad=0"


def test_at_the_defaults_all_is_as_before(game):
    """The sphere doesn't count, near-player is the default, and players may choose"""
    a = game.a
    game.configure()
    status = dict(line.split(": ", 1) for line in a.hangar("status").splitlines())
    assert status["default_load_mode"] == "NearPlayer"
    assert status["players_may_choose_load_place"] == "True"
    assert status["load_clear_of_grid_spheres"] == "False"

    game.configure(DefaultLoadMode="OriginalPosition", LoadOffsetMeters=40)
    game.paste("Old rule")
    ship = game.grid("Old rule")
    saved_id(a.hangar("save Old rule"))
    station_beside(game, ship["center"])
    assert a.hangar("load") == "Loaded Old rule from hangar."
    assert distance(game.grid("Old rule")["pos"], ship["pos"]) < 0.5

    saved_id(a.hangar("save Old rule"))
    teleport(game, game.at(300.0))
    assert a.hangar("load 1 true") == "Loaded Old rule from hangar."
    assert distance(game.grid("Old rule")["pos"], a.position()) < 45
