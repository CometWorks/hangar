using System;
using System.Collections.Generic;
using PluginSdk.Config;
using Shared.Config;

namespace ServerPlugin;

public enum HangarEntryScope
{
    Player,
    Faction,
    Alliance,
    PublicMarket
}

public enum HangarGridKind
{
    Mixed,
    Static,
    Large,
    Small
}

public enum HangarLoadMode
{
    NearPlayer,
    OriginalPosition
}

public struct HangarCooldown
{
    [StructMember("Steam id"), StructCaption]
    public string SteamId { get; set; }

    [StructMember("Last successful save in UTC")]
    public string LastSaveUtc { get; set; }
}

public struct HangarEntry
{
    [StructMember("Stable entry id"), StructCaption]
    public string Id { get; set; }

    [StructMember("Hangar scope")]
    public HangarEntryScope Scope { get; set; }

    [StructMember("Owner Steam id, faction id, or alliance id")]
    public string OwnerId { get; set; }

    [StructMember("Owner display name")]
    public string OwnerName { get; set; }

    [StructMember("Grid display name")]
    public string GridName { get; set; }

    [StructMember("Stored .sbc file relative to storage root")]
    public string BlobPath { get; set; }

    [StructMember("Saved UTC timestamp")]
    public string SavedUtc { get; set; }

    [StructMember("Dominant grid kind")]
    public HangarGridKind GridKind { get; set; }

    [StructMember("Total PCU")]
    public int Pcu { get; set; }

    [StructMember("Total blocks")]
    public int Blocks { get; set; }

    [StructMember("Total grids")]
    public int GridCount { get; set; }

    [StructMember("Static grid count")]
    public int StaticGridCount { get; set; }

    [StructMember("Large dynamic grid count")]
    public int LargeGridCount { get; set; }

    [StructMember("Small grid count")]
    public int SmallGridCount { get; set; }

    [StructMember("Original position X")]
    public double OriginalX { get; set; }

    [StructMember("Original position Y")]
    public double OriginalY { get; set; }

    [StructMember("Original position Z")]
    public double OriginalZ { get; set; }

    [StructMember("Market flag reserved for future market support")]
    public bool ForSale { get; set; }

    [StructMember("Market price reserved for future market support")]
    public long Price { get; set; }

    [StructMember("Market description reserved for future market support")]
    public string Description { get; set; }
}

[Serializable]
[Tab("general", caption: "General")]
[Tab("limits", caption: "Limits")]
[Tab("storage", caption: "Storage")]
[Tab("advanced", caption: "Advanced")]
[Section("runtime", parent: "general", caption: "Runtime")]
[Section("save", parent: "general", caption: "Save And Load")]
[Section("player-limits", parent: "limits", caption: "Player Limits")]
[Section("central", parent: "storage", caption: "Hangar Data")]
[Section("compat", parent: "advanced", caption: "Compatibility")]
public class PluginConfig : PluginSdk.Config.PluginConfig, IPluginConfig
{
    [BoolOption("Enable the Hangar plugin", Parent = "runtime")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    [StringOption(description: "Standalone storage root. Empty uses Space Engineers user data/Hangar. Cluster processes use PluginSdk shared storage automatically.", Parent = "central")]
    public string StorageRoot { get; set => SetField(ref field, value); } = "";

    [BoolOption("Include physically connected subgrids when saving/loading", Parent = "save")]
    public bool IncludeConnectedGrids { get; set => SetField(ref field, value); } = true;

    [BoolOption("Close saved grids after writing them to central storage", Parent = "save")]
    public bool RemoveOriginalOnSave { get; set => SetField(ref field, value); } = true;

    [BoolOption("Transfer loaded grid ownership to the command caller", Parent = "save")]
    public bool TransferOwnershipOnLoad { get; set => SetField(ref field, value); } = true;

    [EnumOption("Default load placement mode", Parent = "save")]
    public HangarLoadMode DefaultLoadMode { get; set => SetField(ref field, value); } = HangarLoadMode.NearPlayer;

    [DoubleOption(1, 10000, "Distance in front of the player for near-player loads", Parent = "save")]
    public double LoadOffsetMeters { get; set => SetField(ref field, value); } = 150;

    [BoolOption("Require the caller to own a majority of functional blocks on the main grid", Parent = "save")]
    public bool RequireMajorityOwnership { get; set => SetField(ref field, value); } = true;

    [BoolOption("Block Keen Services Terminal grid storage/hangar operations", Parent = "compat")]
    public bool BlockVanillaGridStorage { get; set => SetField(ref field, value); } = true;

    [BoolOption("Block built-in or competing hangar chat command handling after this plugin handles it", Parent = "compat")]
    public bool BlockVanillaHangarCommands { get; set => SetField(ref field, value); } = true;

    [ListOption(description: "Command roots swallowed when vanilla hangar blocking is enabled", Parent = "compat")]
    public List<string> BlockedHangarCommandRoots { get; set => SetField(ref field, value); } =
    [
        "hangar",
        "h",
        "factionhangar",
        "fh",
        "alliancehangar",
        "ah",
        "hangaradmin",
        "ha",
        "hangarmarket",
        "hm",
        "market"
    ];

    [IntOption(0, 1000, "Maximum stored grids per player. Zero means unlimited.", Parent = "player-limits")]
    public int MaxEntriesPerPlayer { get; set => SetField(ref field, value); } = 2;

    [IntOption(0, 10080, "Minutes between save commands per player. Zero disables cooldown.", Parent = "player-limits")]
    public int SaveCooldownMinutes { get; set => SetField(ref field, value); } = 60;

    [IntOption(0, int.MaxValue, "Maximum blocks in one stored entry. Zero means unlimited.", Parent = "player-limits")]
    public int MaxBlocksPerEntry { get; set => SetField(ref field, value); } = 0;

    [IntOption(0, int.MaxValue, "Maximum PCU in one stored entry. Zero means unlimited.", Parent = "player-limits")]
    public int MaxPcuPerEntry { get; set => SetField(ref field, value); } = 0;

    [ListOption(description: "Legacy standalone hangar index; cluster entries live in shared plugin storage", Parent = "central")]
    public List<HangarEntry> HangarEntries { get; set => SetField(ref field, value); } = new();

    [ListOption(description: "Legacy standalone cooldowns; cluster cooldowns live in shared plugin storage", Parent = "central")]
    public List<HangarCooldown> Cooldowns { get; set => SetField(ref field, value); } = new();
}
