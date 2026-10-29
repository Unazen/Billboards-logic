# API Contracts

Base path: /api/v1

## Billboards
GET /api/v1/billboards
GET /api/v1/billboards/{id}
GET /api/v1/billboards/{id}/availability

## Videos
GET /api/v1/videos
POST /api/v1/videos/uploads
GET /api/v1/videos/{id}
DELETE /api/v1/videos/{id}

## Placements
GET /api/v1/placements
POST /api/v1/placements
POST /api/v1/placements/validate
POST /api/v1/placements/{id}/publish

## Devices
POST /api/v1/devices/{id}/heartbeat
GET /api/v1/devices/{id}/manifest
POST /api/v1/devices/{id}/playback-events

## Billing
GET /api/v1/billing/wallet
POST /api/v1/billing/top-ups
GET /api/v1/billing/transactions