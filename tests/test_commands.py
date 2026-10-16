"""The !hangar and !h chat commands: what they answer, who may use them, how
entries are picked, bad input, two players' hangars, and the blocking of other
hangar commands. Player A is an administrator, player B has no rights."""

from __future__ import annotations

import re

import pytest

from conftest import saved_id

NO_PERMISSION = "You do not have permission to use that command."
VANILLA_BLOCKED = "Vanilla hangar commands are disabled. Use !hangar."
LINE = re.compile(
    r"(\d+)\. (.+) \[([0-9a-f]{8})\] (\d+) grids, (\d+) blocks, (\d+) PCU, "
    r"saved \d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ"
)


def save_two(game) -> tuple[str, str]:
    """Saves two grids of player A, returns their full ids"""
    game.paste("Alpha one")
    game.paste("Alpha two", offset=(0.0, 0.0, -50.0))
    saved_id(game.a.hangar("save Alpha one"))
    saved_id(game.a.hangar("save Alpha two"))
    return game.a.entry_id(1), game.a.entry_id(2)


def test_empty_hangar(game):
    for command in ("!hangar", "!hangar list", "!h", "!h list"):
        assert game.b.command(command) == "Your hangar is empty."


def test_list_in_save_order(game):
    first, second = save_two(game)
    lines = game.a.hangar("list").splitlines()
    rows = [LINE.fullmatch(line) for line in lines]
    assert all(rows), lines
    assert [(r[1], r[2], r[3], r[4]) for r in rows] == [
        ("1", "Alpha one", first[:8], "1"),
        ("2", "Alpha two", second[:8], "1"),
    ]
    assert game.a.command("!h") == "\n".join(lines)


def test_info(game):
    first, _ = save_two(game)
    info = game.a.hangar("info 1").splitlines()
    assert [line.split(": ", 1)[0] for line in info] == [
        "id",
        "name",
        "owner",
        "saved_utc",
        "kind",
        "grids",
        "blocks",
        "pcu",
        "blob",
        "original_position",
    ]
    values = dict(line.split(": ", 1) for line in info)
    assert values["id"] == first
    assert values["name"] == "Alpha one"
    assert values["owner"] == f"Alpha ({game.a.steam_id})"
    assert values["kind"] == "Large"
    assert values["grids"] == "1 total, 0 static, 1 large, 0 small"
    assert values["blob"] == f"GridBlobs/{game.a.steam_id}/{values['id']}.sbc"


@pytest.mark.parametrize("selector", ["1", "one", '"Alpha ONE"', "<id>"])
def test_selectors(game, selector):
    """By number, part of the name in any case (quoted when it has a space),
    or the start of the id"""
    first, _ = save_two(game)
    selector = selector.replace("<id>", first[:16])
    assert "name: Alpha one" in game.a.hangar("info", selector).splitlines()


def test_multi_word_selector(game):
    save_two(game)
    assert "name: Alpha one" in game.a.hangar("info Alpha one").splitlines()


def test_multi_word_load_with_placement_flag(game):
    """A trailing true or false is the placement flag, not part of the name"""
    save_two(game)
    assert game.a.hangar("load Alpha two true") == "Loaded Alpha two from hangar."
    assert game.a.hangar("load 1 maybe") == "No hangar entry matches '1 maybe'."
    assert game.a.entries() == ["Alpha one"]


def test_remove_with_a_name_that_matches_no_entry(game):
    game.paste("Rover")
    saved_id(game.a.hangar("save Rover"))
    assert game.a.hangar("remove Rover Two") == "No hangar entry matches 'Rover Two'."
    assert game.a.entries() == ["Rover"]


def test_numeric_name_selector(game):
    game.paste("1999")
    saved_id(game.a.hangar("save 1999"))
    assert "name: 1999" in game.a.hangar("info 1999").splitlines()


def test_no_selector_with_one_entry(game):
    game.paste("Only one")
    saved_id(game.a.hangar("save Only one"))
    assert "name: Only one" in game.a.hangar("info").splitlines()


@pytest.mark.parametrize(
    "command, reply",
    [
        ("info", "Select an entry by number, name, or id."),
        ("load", "Select an entry by number, name, or id."),
        ("info 3", "No hangar entry #3."),
        ("info 0", "No hangar entry #0."),
        ("load -1", "No hangar entry #-1."),
        ("remove nothing", "No hangar entry matches 'nothing'."),
        ("info Alpha", "Multiple hangar entries match 'Alpha'. Use number or id."),
    ],
)
def test_bad_selectors(game, command, reply):
    save_two(game)
    assert game.a.hangar(command) == reply
    assert len(game.a.entries()) == 2


def test_selectors_on_an_empty_hangar(game):
    for command in ("info", "info 1", "load 1", "remove 1", "delete x"):
        assert game.a.hangar(command) == "Your hangar is empty."


@pytest.mark.parametrize("command", ["remove", "delete"])
def test_remove_deletes_the_entry_and_its_blob(game, command):
    first, second = save_two(game)
    assert len(game.blobs(game.a)) == 2
    assert game.a.hangar(command, first) == "Removed Alpha one from hangar."
    assert game.a.entries() == ["Alpha two"]
    assert [b.stem for b in game.blobs(game.a)] == [second]
    assert game.grid("Alpha one")["count"] == 0
    assert first not in game.stored()


def test_save_unknown_grid(game):
    assert game.a.hangar("save No such grid") == "Grid not found: No such grid"


def test_save_by_entity_id(game):
    game.paste("By id")
    grid_id = game.grid("By id")["id"]
    assert saved_id(game.a.hangar("save", grid_id))
    assert game.a.entries() == ["By id"]


@pytest.mark.parametrize(
    "command",
    ["remove", "delete"],
)
def test_bad_arguments(game, command):
    reply = game.a.hangar(command)
    assert "Usage: " in reply, reply


def test_unknown_subcommand(game):
    assert game.b.hangar("nosuch") == "Unknown command. Try !hangar help"


@pytest.mark.parametrize("command", ["status", "enable", "disable"])
def test_admin_commands_need_an_admin(game, command):
    assert game.b.hangar(command) == NO_PERMISSION
    assert game.a.hangar("status").splitlines()[0] == "enabled: True"


def test_status(game):
    save_two(game)
    status = dict(line.split(": ", 1) for line in game.a.hangar("status").splitlines())
    assert status == {
        "enabled": "True",
        "storage_root": str(game.server.storage),
        "entries": "2",
        "cooldowns": "1",
        "include_connected_grids": "True",
        "remove_original_on_save": "True",
        "default_load_mode": "NearPlayer",
        "block_vanilla_grid_storage": "True",
        "block_vanilla_hangar_commands": "True",
        "blocked_hangar_roots": "hangar, h, factionhangar, fh, alliancehangar, ah, "
        "hangaradmin, ha, hangarmarket, hm, market",
    }


def test_help_shows_only_what_the_player_may_use(game):
    help_b = game.b.hangar("help")
    help_a = game.a.hangar("help")
    for command in ("list", "info", "save", "load", "remove", "delete"):
        assert f"!hangar {command}" in help_b, help_b
    for command in ("status", "enable", "disable"):
        assert f"!hangar {command}" not in help_b
        assert f"!hangar {command}" in help_a


def test_players_see_only_their_own_hangar(game):
    a, b = game.a, game.b
    first, _ = save_two(game)
    assert b.hangar("list") == "Your hangar is empty."
    assert b.hangar("info", first) == "Your hangar is empty."
    assert b.hangar("load", first) == "Your hangar is empty."

    game.bring_b()
    game.paste("Bravo one", player=b, offset=(0.0, 0.0, 30.0))
    saved_id(b.hangar("save Bravo one"))
    assert b.entries() == ["Bravo one"]
    for command in ("info", "load", "remove"):
        assert b.hangar(command, first) == f"No hangar entry matches '{first}'."
    assert b.hangar("info Alpha") == "No hangar entry matches 'Alpha'."
    assert a.entries() == ["Alpha one", "Alpha two"]
    assert game.grid("Alpha one")["count"] == 0


@pytest.mark.parametrize(
    "command", ["!factionhangar list", "!fh", "/ah load 1", "!market"]
)
def test_other_hangar_commands_are_blocked(game, command):
    assert game.b.command(command) == VANILLA_BLOCKED


def test_blocked_roots_are_configurable(game):
    game.configure(BlockedHangarCommandRoots="grids")
    assert game.b.command("!grids") == VANILLA_BLOCKED
    with pytest.raises(TimeoutError):
        game.b.command("!fh", timeout=5)


def test_blocking_off(game):
    game.configure(BlockVanillaHangarCommands=False)
    with pytest.raises(TimeoutError):
        game.b.command("!fh", timeout=5)


def test_own_commands_are_not_blocked(game):
    """!hangar and !h are on the blocked list, but Hangar answers them alone"""
    a = game.a
    before = a.mark()
    assert a.command("!hangar list") == "Your hangar is empty."
    assert a.command("!h list") == "Your hangar is empty."
    assert VANILLA_BLOCKED not in [t for _, t in a.chat_since(before)]


def test_vanilla_grid_storage_is_patched(game):
    """The Keen Services Terminal's grid storage endpoints and the chat
    handler carry Hangar's patches. A game update that renames one shows here."""
    patched = set(game.a.probe("patched").removeprefix("Probe patched: ").split())
    assert patched >= {
        "OnChatMessageReceived_Server",
        "GetStoredGridsEndpoint",
        "StoreGridTrusted",
        "RetrieveGridTrusted",
        "DeleteGridEndpoint",
    }, patched
