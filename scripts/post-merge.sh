#!/bin/bash
set -e

# Frontend deps (Next.js)
if [ -f delicate-couriers-frontend/package.json ]; then
  ( cd delicate-couriers-frontend && npm install --no-audit --no-fund --prefer-offline )
fi

# Backend deps (.NET) — restore is idempotent. EF migrations run on
# backend startup so we don't apply them here.
if [ -f DelicateCouriers/DelicateCouriers.ApiService/DelicateCouriers.ApiService.csproj ]; then
  dotnet restore DelicateCouriers/DelicateCouriers.ApiService/DelicateCouriers.ApiService.csproj
fi
