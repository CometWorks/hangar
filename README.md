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

`Directory.Build.props.template` is a template for `Directory.Build.props`. The latter is a
local config file (not committed) where you can override the reference folder paths
(`Magnetar` and `Dedicated64`). `setup.py` copies the template to `Directory.Build.props` if
it does not exist yet, then fills in the auto-detected paths. Leaving a path empty falls back
to the platform-specific auto-detection in the file, so the build works on both Windows and
Linux.

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

## Deployment

Use the Magnetar local plugin folder or the included deploy scripts after a build:

```sh
ServerPlugin/Deploy.sh Hangar.dll ServerPlugin/bin/Debug/net10.0
```

On Windows:

```bat
ServerPlugin\Deploy.bat Hangar.dll ServerPlugin\bin\Debug\net10.0
```

`Hangar.xml` is the MagnetarHub metadata file for server-side publication.

Functionality is inspired by and reimplements the original Torch plugin
QuantumHangar by Casimir255 (license: Apache-2.0):
https://github.com/Casimir255/QuantumHangar)
