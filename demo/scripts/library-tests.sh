#!/usr/bin/env bash
set -euo pipefail
mkdir /tmp/work
cp /source/global.json /source/contract.ref /source/Arp.slnx /tmp/work/
cp -R /source/src /source/tests /tmp/work/
cd /tmp/work
dotnet restore Arp.slnx --locked-mode
dotnet build Arp.slnx --no-restore -c Release
dotnet test Arp.slnx --no-restore --no-build -c Release --logger 'trx;LogFileName=demo-002.trx' \
    --results-directory /artifacts/tests