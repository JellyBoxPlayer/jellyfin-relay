# JellyBox Remote Access for Jellyfin

Makes a Jellyfin server reachable from JellyBox outside your home network. The
plugin keeps one outbound connection to Jellybox Cloud, so you don't need to
open any ports.

Requires Jellyfin 10.11.

## Setup

1. Dashboard → Plugins → Repositories → add
   `https://github.com/JellyBoxPlayer/jellyfin-relay/releases/latest/download/manifest.json`.
2. Dashboard → Plugins → Catalog → install JellyBox Remote Access and restart
   Jellyfin.
3. Dashboard → Plugins → JellyBox Remote Access → *Link to Jellybox Cloud*.
4. Open the link the page shows, sign in, and enter the code.

The page then shows the server as connected. JellyBox picks up the remote
address by itself the next time it is used on your home network.

## Installing by hand

```sh
./package.sh
```

Unzip `artifacts/jellybox-remote_<version>.zip` into a new folder inside
Jellyfin's `plugins` folder (`/config/plugins/JellyBox Remote Access` in the
official Docker image) and restart Jellyfin.

## Development

```sh
dotnet build
dotnet test
```

## Releasing

The version and changelog live in `build.yaml`. Bump them, commit, then tag:

```sh
git tag v0.2.0
git push origin v0.2.0
```

The release workflow runs the tests, packages the zip and publishes a GitHub
release with the zip and an updated `manifest.json`. The manifest carries every
earlier version too, so the repository link above keeps working. `manifest.sh`
builds it:

```sh
./manifest.sh <zip> <download url> [previous manifest]
```

## Licence

GPLv3, as Jellyfin plugins must be.
