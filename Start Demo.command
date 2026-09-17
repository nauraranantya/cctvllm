#!/bin/zsh
cd "$(dirname "$0")"
export DOTNET_CLI_HOME="$PWD/../../work/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export OLLAMA_MODELS="$PWD/../../work/ollama-models"
export OLLAMA_NO_CLOUD=1
export OLLAMA_CONTEXT_LENGTH=8192
export OLLAMA_NUM_PARALLEL=1
mkdir -p data
if ! curl -fsS http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
  "$PWD/../../work/ollama/ollama" serve > data/ollama.log 2>&1 &
fi
"$PWD/../../work/dotnet/dotnet" run --no-launch-profile
