#!/bin/sh
# Build the app and the installer on Linux.
# Needs: .NET SDK (dotnet in PATH), Wine, Inno Setup 6 installed in the
# Wine prefix at C:\InnoSetup6 (see README.md).
set -e
cd "$(dirname "$0")"
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-inno}" WINEDEBUG=-all
ZIP=downloads/com0com-2.2.2.0-x64-fre-signed.zip
mkdir -p downloads
[ -f "$ZIP" ] || curl -fsSL -o "$ZIP" https://downloads.sourceforge.net/project/com0com/com0com/2.2.2.0/com0com-2.2.2.0-x64-fre-signed.zip
unzip -oq "$ZIP" -d downloads
(cd .. && dotnet build -c Release)
wine 'C:\InnoSetup6\ISCC.exe' BeijerFXBridge.iss
