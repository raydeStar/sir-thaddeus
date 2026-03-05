#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="Debug"
NO_BUILD=0
NO_TOOLS=0
PASSTHROUGH_ARGS=()

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
HEADLESS_PROJECT="$REPO_ROOT/apps/headless-runtime/SirThaddeus.HeadlessRuntime/SirThaddeus.HeadlessRuntime.csproj"
MCP_PROJECT="$REPO_ROOT/apps/mcp-server/SirThaddeus.McpServer/SirThaddeus.McpServer.csproj"

if [[ -t 1 ]]; then
  C_RESET="\033[0m"
  C_CYAN="\033[36m"
  C_GREEN="\033[32m"
  C_YELLOW="\033[33m"
  C_RED="\033[31m"
else
  C_RESET=""
  C_CYAN=""
  C_GREEN=""
  C_YELLOW=""
  C_RED=""
fi

usage() {
  cat <<'USAGE'
Usage:
  ./dev/terminal.sh [options] [-- <headless-runtime-args...>]

Options:
  -c, --configuration <Debug|Release>  Build/runtime configuration (default: Debug)
      --no-build                       Skip MCP + headless build
      --no-tools                       Start headless runtime without MCP tools
  -h, --help                           Show this help

Examples:
  ./dev/terminal.sh
  ./dev/terminal.sh --no-build
  ./dev/terminal.sh --configuration Release -- --mcp-server /path/to/SirThaddeus.McpServer
USAGE
}

log_step() {
  printf "\n${C_CYAN}==> %s${C_RESET}\n" "$1"
}

fail() {
  printf "${C_RED}ERROR:${C_RESET} %s\n" "$1" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      [[ $# -ge 2 ]] || fail "Missing value for $1"
      CONFIGURATION="$2"
      shift 2
      ;;
    --no-build)
      NO_BUILD=1
      shift
      ;;
    --no-tools)
      NO_TOOLS=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      PASSTHROUGH_ARGS+=("$@")
      break
      ;;
    *)
      PASSTHROUGH_ARGS+=("$1")
      shift
      ;;
  esac
done

case "$CONFIGURATION" in
  Debug|Release) ;;
  *)
    fail "Invalid configuration '$CONFIGURATION'. Use Debug or Release."
    ;;
esac

command -v dotnet >/dev/null 2>&1 || fail "dotnet SDK is required but not found in PATH."
[[ -f "$HEADLESS_PROJECT" ]] || fail "Headless project not found: $HEADLESS_PROJECT"
[[ -f "$MCP_PROJECT" ]] || fail "MCP project not found: $MCP_PROJECT"

cd "$REPO_ROOT"

if [[ "$NO_BUILD" -eq 0 ]]; then
  log_step "Build MCP Server ($CONFIGURATION)"
  dotnet build "$MCP_PROJECT" -c "$CONFIGURATION" -m:1 -v m

  log_step "Build Headless Runtime ($CONFIGURATION)"
  dotnet build "$HEADLESS_PROJECT" -c "$CONFIGURATION" -m:1 -v m
fi

RUNTIME_ARGS=(
  --no-build
  --no-restore
  --project "$HEADLESS_PROJECT"
  --configuration "$CONFIGURATION"
  --
)

if [[ "$NO_TOOLS" -eq 0 ]]; then
  RUNTIME_ARGS+=(--tools)
fi
RUNTIME_ARGS+=("${PASSTHROUGH_ARGS[@]}")

printf "\n${C_GREEN}Launching headless terminal runtime...${C_RESET}\n"
printf "${C_YELLOW}  /help for commands, /exit to quit.${C_RESET}\n\n"

exec dotnet run "${RUNTIME_ARGS[@]}"
