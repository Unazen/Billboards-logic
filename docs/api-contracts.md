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

## Участник 3: реализованные эндпоинты

Ошибки: `{ "code", "message", "details" }`; 400 валидация, 401, 403, 404, 409 конфликт.
Авторизация: `Authorization: Bearer <accessToken>` (из `/account/login`); устройства — `X-Device-Token`.

### Аккаунт и тарифы
POST /api/v1/account/register
POST /api/v1/account/login
GET  /api/v1/account/me
GET  /api/v1/tariffs                      (публично)
GET  /api/v1/tariffs/current
GET/POST/PATCH /api/v1/admin/tariffs[/{id}]

### Билборды
GET  /api/v1/billboards, /api/v1/billboards/{id}
GET/POST/PATCH /api/v1/admin/billboards[/{id}]
POST /api/v1/admin/billboards/{id}/disable
POST /api/v1/admin/billboards/{id}/rotate-device-token

### Видео
GET  /api/v1/videos
POST /api/v1/videos/uploads               (шаг 1: задача + проверка тарифа)
PUT  /api/v1/videos/{id}/content          (шаг 2: файл телом запроса, octet-stream)
POST /api/v1/videos/import-url            (с защитой от SSRF)
GET  /api/v1/videos/{id}, /download
DELETE /api/v1/videos/{id}                (409 VIDEO_IN_USE при активных размещениях)

### Финансы
GET  /api/v1/billing/wallet
POST /api/v1/billing/top-ups              (идемпотентно по IdempotencyKey)
GET  /api/v1/billing/transactions
GET  /api/v1/billing/quote/{placementId}?billboardCount=
POST /api/v1/billing/subscriptions, /{id}/cancel
GET  /api/v1/billing/earnings             (Admin)
GET  /api/v1/billing/usage                (показы и затраты)

### Устройства (X-Device-Token)
POST /api/v1/devices/{id}/heartbeat
POST /api/v1/devices/{id}/playback-events (дубликаты не списываются повторно)

### Администрирование
GET   /api/v1/admin/users
PATCH /api/v1/admin/users/{id}/active     { "isActive": bool }
PATCH /api/v1/admin/users/{id}/role       { "role": "Admin|Client" }
GET   /api/v1/admin/logs?action=&userId=&from=&to=&skip=&take=
GET   /api/v1/admin/logs/export           (CSV)
GET   /api/v1/admin/devices               (мониторинг)
