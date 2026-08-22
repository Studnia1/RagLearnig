#!/usr/bin/env bash
# Skopiuj do start.sh, uzupełnij dane i odpalaj trackera jednym poleceniem:
#   cp start.example.sh start.sh   (start.sh jest w .gitignore — token nie trafi do repo)
#   ./start.sh
export TELEGRAM_BOT_TOKEN="TU_WKLEJ_TOKEN"
export TELEGRAM_CHAT_ID="TU_WKLEJ_CHAT_ID"
cd "$(dirname "$0")"
dotnet run --project src/VintedTracker
