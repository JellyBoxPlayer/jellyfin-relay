#!/bin/sh
set -eu
cd "$(dirname "$0")"

version=$(sed -n 's/^version: "\(.*\)"/\1/p' build.yaml)
out=artifacts/jellybox-remote_$version
rm -rf "$out" "$out.zip"

dotnet publish Jellyfin.Plugin.JellyboxRemote -c Release -o "$out" -p:Version="$version" --nologo
find "$out" -type f ! -name 'Jellyfin.Plugin.JellyboxRemote.dll' -delete
zip -qj "$out.zip" "$out"/*

echo "artifacts/jellybox-remote_$version.zip"
