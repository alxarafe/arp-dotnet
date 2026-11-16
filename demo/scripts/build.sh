#!/usr/bin/env bash
set -euo pipefail
mkdir /tmp/work
cp /source/.editorconfig /source/global.json /source/contract.ref /tmp/work/
cp -R /source/src /source/demo /tmp/work/
cd /tmp/work
dotnet restore demo/DictionaryHost/DictionaryHost.csproj --locked-mode
dotnet format whitespace demo/DictionaryHost/DictionaryHost.csproj --verify-no-changes --no-restore \
    --include demo/DictionaryHost/Program.cs
dotnet publish demo/DictionaryHost/DictionaryHost.csproj --no-restore -c Release -o /artifacts/host