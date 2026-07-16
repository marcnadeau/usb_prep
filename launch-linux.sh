#!/usr/bin/env bash
set -euo pipefail

# Always run from repository root, even when launched elsewhere.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

dotnet run --project "MediaFileAnalyzer.csproj" -c Debug