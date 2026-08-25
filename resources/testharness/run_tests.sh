#!/bin/bash
# Runs the pure-logic EditMode tests of SidequestV1 (speed/estimator stack)
# and SidequestCV (CV pipeline + ride simulations) WITHOUT the Unity editor,
# using the Roslyn compiler and .NET runtime that ship inside the Unity
# install. This is the only way to run tests while the editor holds the
# project lock (Temp/UnityLockfile).
#
# Usage:  ./run_tests.sh [v1|cv|all]        (default: all)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
UNITY_VERSION="${UNITY_VERSION:-6000.3.20f1}"
SCRIPTING="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/Resources/Scripting"
DOTNET="$SCRIPTING/NetCoreRuntime/dotnet"
CSC="$SCRIPTING/DotNetSdkRoslyn/csc.dll"
FW="$(ls -d "$SCRIPTING"/NetCoreRuntime/shared/Microsoft.NETCore.App/* | tail -1)"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

emit_common() {
  echo "-nologo"
  echo "-target:exe"
  echo "-nostdlib"
  for dll in System.Runtime System.Private.CoreLib System.Console System.Linq \
             System.Collections System.Runtime.Extensions System.Reflection \
             System.Reflection.Primitives System.Reflection.Extensions \
             System.IO.FileSystem; do
    echo "-r:$FW/$dll.dll"
  done
  echo "$HERE/UnityShim.cs"
  echo "$HERE/NUnitShim.cs"
  echo "$HERE/Runner.cs"
}

write_runtimeconfig() {
  cat > "$1" <<'EOF'
{
  "runtimeOptions": {
    "tfm": "net6.0",
    "framework": { "name": "Microsoft.NETCore.App", "version": "6.0.0" }
  }
}
EOF
}

run_v1() {
  local A="$REPO/SidequestV1/Assets"
  local RSP="$OUT/v1.rsp"
  {
    emit_common
    echo "-out:$OUT/v1tests.dll"
    for f in MovementEstimator MovementEstimatorSettings DeviceMotionReading \
             GpsHealth GpsSpeedWindow GeoDistanceCalculator DelayedSpeedBuffer \
             TimedSpeedSample VehicleSpeedFilter; do
      echo "$A/Scripts/Speed/$f.cs"
    done
    for t in MovementEstimatorTests GroundTransportSimulationTests \
             BartRideSimulationTests GpsSpeedWindowTests \
             GeoDistanceCalculatorTests DelayedSpeedBufferTests \
             VehicleSpeedFilterTests; do
      echo "$A/Tests/EditMode/$t.cs"
    done
  } > "$RSP"
  "$DOTNET" "$CSC" @"$RSP"
  write_runtimeconfig "$OUT/v1tests.runtimeconfig.json"
  echo "== SidequestV1 =="
  "$DOTNET" "$OUT/v1tests.dll"
}

run_cv() {
  local A="$REPO/SidequestCV/Assets"
  local RSP="$OUT/cv.rsp"
  {
    emit_common
    echo "-out:$OUT/cvtests.dll"
    for f in Detection LetterboxMath YoloxPostProcessor CvClassCatalog \
             SurfaceAnalyzer ObstacleDirector CollisionJudge DetectionEventMapper; do
      echo "$A/Scripts/CV/$f.cs"
    done
    for t in CvFixtures CvYoloxPostProcessorTests CvSurfaceAnalyzerTests \
             CvObstacleDirectorTests CvMappingAndCollisionTests \
             CvRideSimulationTests; do
      echo "$A/Tests/EditMode/$t.cs"
    done
  } > "$RSP"
  "$DOTNET" "$CSC" @"$RSP"
  write_runtimeconfig "$OUT/cvtests.runtimeconfig.json"
  echo "== SidequestCV =="
  CV_FIXTURES="$A/Tests/Fixtures" "$DOTNET" "$OUT/cvtests.dll"
}

case "${1:-all}" in
  v1) run_v1 ;;
  cv) run_cv ;;
  all) run_v1 && run_cv ;;
  *) echo "usage: $0 [v1|cv|all]" >&2; exit 2 ;;
esac
