#!/bin/sh
set -eu
cd "$(dirname "$0")"

zip=$1
url=$2
previous=${3:-}

field() {
  sed -n "s/^$1: \"\(.*\)\"/\1/p" build.yaml
}

checksum=$(openssl dgst -md5 -r "$zip" | cut -d' ' -f1)
versions='[]'
if [ -n "$previous" ]; then
  versions=$(jq '.[0].versions' "$previous")
fi

jq -n \
  --arg guid "$(field guid)" \
  --arg name "$(field name)" \
  --arg description "$(field description)" \
  --arg overview "$(field overview)" \
  --arg owner "$(field owner)" \
  --arg category "$(field category)" \
  --arg version "$(field version)" \
  --arg changelog "$(field changelog)" \
  --arg targetAbi "$(field targetAbi)" \
  --arg url "$url" \
  --arg checksum "$checksum" \
  --arg timestamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --argjson versions "$versions" \
  '[{
    guid: $guid,
    name: $name,
    description: $description,
    overview: $overview,
    owner: $owner,
    category: $category,
    versions: ([{
      version: $version,
      changelog: $changelog,
      targetAbi: $targetAbi,
      sourceUrl: $url,
      checksum: $checksum,
      timestamp: $timestamp
    }] + ($versions | map(select(.version != $version))))
  }]'
