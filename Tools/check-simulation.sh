#!/bin/sh
set -eu
hospital_repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$hospital_repo_dir"
MSBUILDUSESERVER=0 dotnet build Tests/Game.Simulation.Tests/Game.Simulation.Tests.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false -v minimal
MSBUILDUSESERVER=0 dotnet build Tools/HeadlessChecks/HeadlessChecks.csproj --source Tools/HeadlessChecks --disable-build-servers -m:1 -p:UseSharedCompilation=false -v minimal
dotnet Tools/HeadlessChecks/bin/Debug/net10.0/HeadlessChecks.dll "$hospital_repo_dir/Tests/Game.Simulation.Tests/bin/Debug/net10.0/Game.Simulation.Tests.dll" "$@"
