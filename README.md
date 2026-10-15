# Hangar

Server-only Space Engineers plugin for Magnetar.

## Prerequisites

- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Projects

- `ServerPlugin` - Magnetar plugin entry point and server runtime code.
- `Shared` - common plugin helpers and interfaces.

This repository builds only the Magnetar server plugin.

## Build

Install the Space Engineers Dedicated Server build references and the .NET SDK required by the project, then build:

```sh
dotnet build Hangar.sln -c Debug
```

The plugin output is:

```text
ServerPlugin/bin/Debug/net10.0/Hangar.dll
```

The plugin version lives in `Version.Build.props`, which **is** committed and imported by
`Directory.Build.props`. Bump the version there at a single place.

`Directory.Build.props` auto-detects the Dedicated Server (`Dedicated64`) and the Magnetar
installation (`Magnetar`) on Windows and Linux. To override them, set them in
`Directory.Build.props.user`, which is not committed. `setup.py` writes that file with the
auto-detected Dedicated Server folder.

## Configuration

Magnetar stores configuration through the Plugin SDK config system.
An empty `StorageRoot` keeps the declared default in configuration and resolves to
`Space Engineers user data/Hangar` on a standalone server.

In a cluster, Hangar ignores `StorageRoot` and uses
`PluginStorage.GetSharedDirectory` automatically. Grid blobs and per-player entry
indexes/cooldowns live there; operational changes never rewrite the canonical
Plugin SDK configuration on individual nodes. Per-player file locks coordinate
slot limits, cooldowns and consumption across nodes. Broadcasts are not used as
the source of truth because starting nodes would miss earlier messages.

The cluster launcher must set `CLUSTER_SHARED_ROOT`. For several Host machines,
that path must be the same shared filesystem on every Host and support advisory
file locks and atomic rename. Quasar currently provides the path automatically
for a single Host; its multi-Host setup does not provision a shared mount.
Hangar refuses to start in a cluster when legacy config entries or cooldowns
are present. This version does not migrate them. Keep the original standalone
config and grid blobs intact; start with an empty cluster Hangar or migrate the
data separately before enabling the plugin there.

## Development and deployment

Load the working copy through a Magnetar development folder: start Magnetar with `-sources`
and add the repository with the Sources button. Magnetar then compiles the plugin from source.

Builds deploy nothing by default. To copy the build into `<MagnetarData>/Local`, set
`MagnetarData` (the Magnetar config folder) in `Directory.Build.props.user` or pass it to a
single build:

```sh
dotnet build Hangar.sln -p:MagnetarData=$HOME/.config/Magnetar/Magnetar
```

`Hangar.xml` is the MagnetarHub metadata file for server-side publication.

## Tests

`tests/` holds an in-game test suite. It starts a Magnetar server with this working copy
of the plugin, joins it with two headless game clients and drives them through the
[Remote plugin](https://github.com/CometWorks/remote): player A is an administrator,
player B has no rights. The tests paste grids, run the chat commands and check the
results on the server, in the stored files and in the replies. It runs on Linux only.

What it covers:

- Saving a grid by name, by entity id or by looking at it, and loading it back: blocks,
  inventory, static grids, ownership with and without the transfer to the loader, near
  the player or at the original position, keeping the original on save, rotor heads and
  docked ships with and without connected grids, and a pilot getting out
  (`test_save_load.py`)
- Who may save what and how often: majority ownership, the slot, block and PCU limits,
  the save cooldown, and the plugin switched off (`test_rules.py`)
- The `!hangar` and `!h` commands: list, info, entry selectors, remove and delete,
  status, enable and disable, permissions, bad input, two players' hangars, the
  blocking of other hangar commands, and the patches on the vanilla grid storage
  (`test_commands.py`)
- Stored grids, cooldowns and config across a server restart, and a broken config file
  (`test_restart.py`)
- Two cluster nodes sharing one storage: entries, slot limit and cooldown across nodes,
  and a node with legacy standalone entries (`test_cluster.py`)

The world is the game's Empty World with the two test players, written fresh for every
run; each test pastes its own grids at a spot of its own. `tests/probe` is a test-only
server plugin the server loads next to Hangar: it reports grids, owners and inventories
on the server, changes the plugin config and stored cooldowns at runtime, and seats,
moves and connects things the clients can't, so the tests need no restart for each
config.

The cluster tests run two Magnetar servers with `CLUSTER_NODE_ID` and one
`CLUSTER_SHARED_ROOT`, which is what puts Hangar into its cluster storage mode. There is
no gateway or World Authority, and player A is on both nodes at once from two clients.

### Running

You need the Space Engineers client and Dedicated Server from Steam, Magnetar and Pulsar
installed, [uv](https://docs.astral.sh/uv/), and working copies of Remote and
[DirectTransport](https://github.com/CometWorks/direct-transport). The rig takes the
ports, client ids and folders from a test slot of the `se` workspace
(`notes/parallel-test-work`), and waits for free memory before each process it starts.

```bash
uv run pytest
```

Where GitHub's API rate limits Magnetar, export `PULSAR_GITHUB_TOKEN` for it to download
its hub plugins with. `HG_SKIP_CLUSTER=1` leaves out the cluster tests.

A full run takes about 25 minutes, a third of it the server restarts and the cluster nodes. `tests/rig.py` lists the environment variables that
point the rig at other installations and working copies. To keep the server and clients
running between runs while you work on a test:

```bash
uv run python tests/session.py start
HG_ATTACH=1 HG_KEEP=1 uv run pytest tests/test_commands.py
uv run python tests/session.py stop
```

Functionality is inspired by and reimplements the original Torch plugin
QuantumHangar by Casimir255 (license: Apache-2.0):
https://github.com/Casimir255/QuantumHangar)
