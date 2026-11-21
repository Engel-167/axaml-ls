#!/usr/bin/env bash
# Benchmarks the server on a generated big project (docs/plans/PERFORMANCE.md, P0.1).
#
# usage: scripts/bench.sh [new.dll] [--base-ref REF | --no-base] [more lsp-bench.py options]
#
# Builds the working tree's server (unless a dll is given) and, as the reference, the server at git REF (default:
# main) in a temporary worktree. Both run side by side on the same generated project; exits 1 on a regression
# (see scripts/lsp-bench.py). Everything temporary is deleted afterwards.
# --no-base measures the new server alone; add `--out FILE` to record numbers (docs/plans/bench-baseline.json).
set -euo pipefail
cd "$(dirname "$0")/.."

dll=""
base_ref="main"
pass=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    *.dll) dll="$1" ;;
    --base-ref) base_ref="$2"; shift ;;
    --no-base) base_ref="" ;;
    *) pass+=("$1") ;;
  esac
  shift
done

dir="$(mktemp -d)"
cleanup() {
  git worktree remove --force "$dir/base-src" >/dev/null 2>&1 || true
  rm -rf "$dir"
}
trap cleanup EXIT

if [[ -z "$dll" ]]; then
  dotnet build src/AxamlLs -c Release -v q -nologo >/dev/null
  dll="src/AxamlLs/bin/Release/net10.0/AxamlLs.dll"
fi

base=()
if [[ -n "$base_ref" ]]; then
  echo "building the reference server at $base_ref" >&2
  git worktree add --detach "$dir/base-src" "$base_ref" >/dev/null 2>&1
  dotnet build "$dir/base-src/src/AxamlLs" -c Release -v q -nologo -o "$dir/base-bin" >/dev/null
  base=(--base "$dir/base-bin/AxamlLs.dll")
fi

python3 scripts/make-big-project.py "$dir/big" >/dev/null
python3 scripts/lsp-bench.py "$dll" "$dir/big/SampleApp" "${base[@]}" "${pass[@]}"
