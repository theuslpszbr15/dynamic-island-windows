#!/usr/bin/env bash
# Reads versionCode / versionName from android/app/build.gradle.kts with the SAME patterns
# DynamicIsland.csproj uses for the AndroidVersionCode / AndroidVersionName assembly metadata,
# so the release notes, the exe and the APK can never disagree. Fails if either is missing or ambiguous.
set -euo pipefail
gradle="${1:-android/app/build.gradle.kts}"
code_re='versionCode[[:space:]]*=[[:space:]]*([0-9]+)'      # csproj: versionCode\s*=\s*(\d+)
name_re='versionName[[:space:]]*=[[:space:]]*"([^"]+)"'     # csproj: versionName\s*=\s*"([^"]+)"
text="$(cat "$gradle")"
# csproj takes the first match; we also refuse a second assignment, which would make "first" a guess.
[ "$(grep -Ec "$code_re" "$gradle")" = 1 ] || { echo "::error::expected exactly one versionCode assignment in $gradle" >&2; exit 1; }
[[ "$text" =~ $code_re ]] || { echo "::error::versionCode not found in $gradle" >&2; exit 1; }
code="${BASH_REMATCH[1]}"
[[ "$text" =~ $name_re ]] || { echo "::error::versionName not found in $gradle" >&2; exit 1; }
name="${BASH_REMATCH[1]}"
echo "versionCode=$code"
echo "versionName=$name"
