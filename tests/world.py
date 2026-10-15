"""The test world, the game's Empty World with the test players, and the
blueprints of the grids the tests paste.

The world has no grids of its own: Hangar takes grids out of the world, so each
test pastes the grids it needs, under names of its own.

The players exist before the clients join, so their identity ids are known.
"""

from __future__ import annotations

import re
import shutil
from pathlib import Path

PLAYER_A = 144115188075870001
PLAYER_B = 144115188075870002

SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "PUBLIC",
    "MaxPlayers": "8",
    "EnableRespawnShips": "false",
    "EnableSpaceSuitRespawn": "true",
    "TrashRemovalEnabled": "false",
    "EnableCopyPaste": "true",
    "BlockLimitsEnabled": "NONE",
    "EnableResearch": "false",
    "AutoSaveInMinutes": "0",
    "ExperimentalMode": "true",
    "EnableSunRotation": "false",
    # The Empty World's NPC encounters come and go near the players
    "EnableEncounters": "false",
    "GlobalEncounterCap": "0",
    "CargoShipsEnabled": "false",
    "EnableContainerDrops": "false",
    "EnableDrones": "false",
    "EnablePlanetaryEncounters": "false",
}


def _identity(identity_id: int, name: str) -> str:
    return (
        "<MyObjectBuilder_Identity>"
        f"<IdentityId>{identity_id}</IdentityId><DisplayName>{name}</DisplayName>"
        "<CharacterEntityId>0</CharacterEntityId>"
        "<BlockLimitModifier>0</BlockLimitModifier>"
        "</MyObjectBuilder_Identity>"
    )


def _player(steam_id: int, identity_id: int, name: str) -> str:
    return (
        '<item><Key xsi:type="PlayerId">'
        f"<ClientId>{steam_id}</ClientId><SerialId>0</SerialId></Key>"
        '<Value xsi:type="MyObjectBuilder_Player">'
        f"<DisplayName>{name}</DisplayName><IdentityId>{identity_id}</IdentityId>"
        "<Connected>false</Connected></Value></item>"
    )


def _insert(text: str, tag: str, content: str) -> str:
    """Puts content at the start of the first <tag> element, opening an empty
    <tag /> if need be"""
    if f"<{tag}>" in text:
        return text.replace(f"<{tag}>", f"<{tag}>{content}", 1)
    new, count = re.subn(rf"<{tag}\s*/>", f"<{tag}>{content}</{tag}>", text, count=1)
    assert count == 1, tag
    return new


def _set(text: str, name: str, value: str) -> str:
    new, count = re.subn(
        rf"<{name}>[^<]*</{name}>|<{name}\s*/>", f"<{name}>{value}</{name}>", text
    )
    if count:
        return new
    # Settings the template leaves at their default
    return text.replace("</Settings>", f"<{name}>{value}</{name}></Settings>", 1)


def prepare(template: Path, world: Path, players: dict[int, tuple[int, str]]) -> None:
    """A fresh copy of the template world with the players (steam id ->
    (identity id, name))"""
    if world.exists():
        shutil.rmtree(world)
    shutil.copytree(template, world)
    (world / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    (world / "Sandbox.sbcB5").unlink(missing_ok=True)

    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        text = path.read_text(encoding="utf-8")
        for key, value in SETTINGS.items():
            text = _set(text, key, value)
        if name == "Sandbox.sbc":
            identities = {i: n for i, n in players.values()}
            text = _insert(
                text,
                "Identities",
                "".join(_identity(i, n) for i, n in identities.items()),
            )
            text, count = re.subn(
                r"<AllPlayersData>(\s*)<dictionary>",
                lambda m: "<AllPlayersData><dictionary>"
                + "".join(_player(s, i, n) for s, (i, n) in players.items()),
                text,
                count=1,
            )
            assert count == 1, "AllPlayersData"
        path.write_text(text, encoding="utf-8")


# ---------------------------------------------------------------------------
# Blueprints
# ---------------------------------------------------------------------------


def vec(tag: str, v) -> str:
    return f'<{tag} x="{v[0]}" y="{v[1]}" z="{v[2]}" />'


def block(
    xsi_type: str,
    subtype: str,
    cell,
    *,
    forward: str = "Forward",
    up: str = "Up",
    extra: str = "",
) -> str:
    """A cube block; the paste makes the pasting player its owner"""
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi_type}">'
        f"<SubtypeName>{subtype}</SubtypeName>{vec('Min', cell)}"
        f'<BlockOrientation Forward="{forward}" Up="{up}" />{extra}'
        "</MyObjectBuilder_CubeBlock>"
    )


def armor(cells) -> str:
    return "".join(block("CubeBlock", "LargeBlockArmorBlock", c) for c in cells)


def row(n: int) -> list:
    """Cells (0..n-1, 0, 0)"""
    return [(x, 0, 0) for x in range(n)]


def grid_xml(name: str, blocks: str, static: bool = False) -> str:
    return (
        "<CubeGrid><SubtypeName /><EntityId>0</EntityId>"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        '<PositionAndOrientation><Position x="0" y="0" z="0" />'
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        f"<IsStatic>{str(static).lower()}</IsStatic>"
        f"<IsUnsupportedStation>{str(static).lower()}</IsUnsupportedStation>"
        f"<DisplayName>{name}</DisplayName></CubeGrid>"
    )


def blueprint(grid: str) -> str:
    """A bp.sbc document of one grid (grid_xml)"""
    return (
        '<?xml version="1.0"?>'
        '<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        '<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="Test" />'
        f"<CubeGrids>{grid}</CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>"
    )


# The grids the tests paste, as block XML. A ship: armor, a battery, a cargo
# container and a cockpit facing -z; three terminal blocks to own.
def ship() -> str:
    return (
        armor([(0, 0, 0), (1, 0, 0), (2, 0, 0), (0, 0, 1), (2, 0, 1)])
        + block("BatteryBlock", "LargeBlockBatteryBlock", (0, 1, 0))
        + block("CargoContainer", "LargeBlockSmallContainer", (1, 0, 1))
        + block("Cockpit", "LargeBlockCockpit", (1, 1, 0))
    )


SHIP_BLOCKS = 8


def rotor_base() -> str:
    """Armor and a rotor base; the probe builds its head, a grid of its own"""
    return armor(row(3)) + block(
        "MotorStator", "LargeStator", (1, 1, 0), extra="<Enabled>true</Enabled>"
    )


# A dock and a ship whose connectors face each other one cell apart, for the
# probe to lock. The dock's connector at (2, 0, 0) faces +x; the docked ship
# starts at the dock's (3, 0, 0), its connector at its own (0, 0, 0) faces -x.
def dock() -> str:
    return armor(row(2)) + block(
        "ShipConnector",
        "Connector",
        (2, 0, 0),
        forward="Right",
        extra="<Enabled>true</Enabled>",
    )


def docked_ship() -> str:
    return block(
        "ShipConnector",
        "Connector",
        (0, 0, 0),
        forward="Left",
        extra="<Enabled>true</Enabled>",
    ) + armor([(1, 0, 0), (2, 0, 0)])


DOCKED_OFFSET = (7.5, 0, 0)
