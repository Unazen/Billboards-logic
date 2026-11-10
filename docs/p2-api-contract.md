# P2 ↔ P3 API contract

Документ фиксирует фактический контракт backend P3 и то, как к нему адаптирован
симулятор P2. Источник истины для endpoint'ов — код P3:
`Billboard.Application.Contracts` и `Billboard.Api.Controllers`.

- Base path: `/api/v1`.
- API в Docker Compose: `http://localhost:8080`.
- JSON — `camelCase`, моменты времени — ISO 8601 с offset.
- Пользователь: `Authorization: Bearer <accessToken>`.
- Устройство/симулятор: `X-Device-Token: "{billboardId:N}.{secret}"`.

Токен устройства выдаётся один раз при создании билборда или ротации токена
(`POST /api/v1/admin/billboards/{id}/rotate-device-token`).

## 1. Устройства

### 1.1. Manifest

```http
GET /api/v1/devices/{billboardId}/manifest
X-Device-Token: d2f3f8d96a3e4e9198772b8e0a5f4321.<secret>
```

Фактический ответ P3:

```json
{
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "version": 42,
  "generatedAt": "2026-11-09T12:00:00+00:00",
  "cacheLimitBytes": 34359738368,
  "cacheUsedBytes": 0,
  "files": [
    {
      "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "placementId": "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa",
      "minimumPlaysPerHour": 6,
      "fileName": "promo-42.mp4",
      "sizeBytes": 104857600,
      "durationSeconds": 15,
      "checksum": "9f2c...",
      "downloadUrl": "http://minio:9000/billboard-videos/...",
      "urlExpiresAt": "2026-11-09T12:15:00+00:00"
    }
  ]
}
```

Симулятор P2 преобразует файл так:

| P3 `files[]` | P2 `ManifestItem` |
|---|---|
| `videoAssetId` | `VideoId` |
| `placementId` | `PlacementId` |
| `minimumPlaysPerHour` | `PlaysPerHour` |
| `fileName` | `FileName` |
| `sizeBytes` | `SizeBytes` |
| `durationSeconds` | `DurationSeconds` |
| `checksum` | `ChecksumSha256` |
| `downloadUrl` | `DownloadUrl` |
| `urlExpiresAt` | `DownloadUrlExpiresAt` |
| `version` (long) | `PlaybackManifest.Version` (string) |

`cacheLimitBytes`/`cacheUsedBytes` P3 не переопределяют локальный лимит P2:
симулятор использует `--cache-mb` (по умолчанию 32 ГБ).

P3 отдаёт **отдельную запись на каждое размещение**. Если один ролик используется
в двух размещениях, в `files` будет две записи с одинаковым `videoAssetId`, но
разными `placementId` и `minimumPlaysPerHour`. P2:

- скачивает файл один раз (кэш по `videoAssetId`);
- создаёт отдельную позицию ротации для каждого `placementId`;
- отправляет событие показа с правильным `placementId`.

Это позволяет соблюдать квоты и списывать деньги по конкретному размещению.

### 1.2. Heartbeat

```http
POST /api/v1/devices/{billboardId}/heartbeat
X-Device-Token: ...
Content-Type: application/json
```

P3 принимает только два поля:

```json
{
  "simulatorVersion": "1.0.0",
  "status": "Online"
}
```

Ответ — `204 No Content`. P3 обновляет `LastSeenAt`, статус и пишет
`DeviceHeartbeat`. P2 отправляет heartbeat каждые 30 секунд (настраивается
`--heartbeat`).

### 1.3. События показов

```http
POST /api/v1/devices/{billboardId}/playback-events
X-Device-Token: ...
Content-Type: application/json
```

```json
{
  "events": [
    {
      "idempotencyKey": "5f0c2c1e22224a3b9a10bbbbbbbbbbbb",
      "placementId": "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa",
      "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "startedAt": "2026-11-09T12:00:10+00:00",
      "durationSeconds": 15,
      "result": "Completed"
    }
  ]
}
```

`result`: `Completed`, `Skipped`, `Error`. P2 `PlaybackResult.Success` маппится в
`Completed`, `Failed` — в `Error`, `Skipped` — в `Skipped`.

Ответ P3 — счётчики, без списка ID:

```json
{
  "accepted": 1,
  "duplicates": 0,
  "rejected": 0,
  "errors": []
}
```

P2 считает пакет обработанным, если `accepted + duplicates + rejected` равно
размеру пакета. `rejected` — постоянная ошибка (например, размещение не
относится к билборду), повторять её бессмысленно; P2 удаляет такие события из
outbox и пишет ошибку в лог. `duplicates` — нормальный результат повторной
отправки после сбоя.

P3 хранит уникальный ключ в виде `"{billboardId:N}:{idempotencyKey}"` и
списывает деньги только за `Completed`.

### 1.4. Отчёт о кэше

```http
POST /api/v1/devices/{billboardId}/cache-reports
X-Device-Token: ...
```

```json
{
  "items": [
    {
      "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "sizeBytes": 104857600,
      "downloadStatus": "Ready",
      "integrityVerified": true
    }
  ]
}
```

Ответ:

```json
{
  "cacheUsedBytes": 104857600,
  "cacheLimitBytes": 34359738368,
  "files": 1
}
```

P2 отправляет отчёт после каждой синхронизации manifest. P3 сохраняет
`DeviceCachedVideo`; неизвестные `videoAssetId` возвращают `400 UNKNOWN_VIDEO`.

## 2. Проверка расписания

P3 использует собственные DTO (не DTO P2), поэтому интерфейс должен
ориентироваться на них.

```http
POST /api/v1/placements/validate
Authorization: Bearer <user token>
```

```json
{
  "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "startAt": "2026-11-09T09:00:00+03:00",
  "endAt": "2026-11-13T18:00:00+03:00",
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  "startTimeOfDay": "09:00:00",
  "endTimeOfDay": "18:00:00",
  "minimumPlaysPerHour": 6
}
```

Ответ:

```json
{
  "available": false,
  "quote": {
    "placementId": "00000000-0000-0000-0000-000000000000",
    "billableHours": 40,
    "expectedPlays": 240,
    "pricePerPlay": 5,
    "discountPercent": 0,
    "pricePerPlayAfterDiscount": 5,
    "totalAmount": 1200,
    "currency": "RUB"
  },
  "conflicts": [
    {
      "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
      "reason": "INSUFFICIENT_ADVERTISING_CAPACITY",
      "message": "Не хватает рекламного времени: 09.11.2026 10:00 требуется 3800 с/ч, доступно 3600 с/ч."
    }
  ],
  "alternatives": [
    {
      "startAt": "2026-11-09T12:00:00+03:00",
      "endAt": "2026-11-09T21:00:00+03:00"
    }
  ]
}
```

`POST /api/v1/placements/{id}/publish` возвращает
`PublishPlacementResultDto`:

```json
{
  "placementId": "...",
  "status": "Scheduled",
  "reservedAmount": 1200,
  "quote": { }
}
```

`GET /api/v1/placements/{id}/alternatives?max=3` возвращает
`[{ "startAt": "...", "endAt": "..." }]`.

### 2.1. Соответствие моделей P2 и P3

| P2 `ScheduleValidationResult` | P3 `PlacementValidationResultDto` |
|---|---|
| `IsAvailable` | `Available` |
| `Conflicts[].Code` | `Conflicts[].Reason` |
| `Conflicts[].Message` | `Conflicts[].Message` |
| `Alternatives[].StartDate + StartTime` | `Alternatives[].StartAt` |
| `Alternatives[].EndDate + EndTime` | `Alternatives[].EndAt` |
| `Summary` | нет прямого аналога (P3 считает quote) |

| P2 `PlacementRequest` | P3 `PlacementUpsertRequest` |
|---|---|
| `BillboardId` | `BillboardId` |
| `VideoAssetId` | `VideoAssetId` |
| `StartDate + StartTime` | `StartAt` |
| `EndDate + EndTime` | `EndAt` |
| `DaysOfWeek` | `DaysOfWeek` (строки) |
| `MinimumPlaysPerHour` | `MinimumPlaysPerHour` |
| `VideoDurationSeconds` | берётся из `VideoAsset` |
| `EstimatedCost`/`AvailableBudget` | quote/бюджет P3 |

Важно: `DaysOfWeekMask` P2 и P3 имеют разный порядок битов. P2 использует
`System.DayOfWeek` (бит 0 — воскресенье), P3 `WeekDays` (бит 0 — понедельник).
При передаче маски между модулями нужна конвертация, а не прямое копирование.

### 2.2. Коды конфликтов P3

| `reason` | Значение |
|---|---|
| `BILLBOARD_DISABLED` | билборд отключён администратором |
| `VIDEO_NOT_READY` | видео ещё обрабатывается |
| `VIDEO_DURATION_UNKNOWN` | неизвестна длительность ролика |
| `NO_ACTIVE_HOURS` | дни недели не пересекаются с периодом |
| `INSUFFICIENT_ADVERTISING_CAPACITY` | суммарная ёмкость часа превышена |

P2-коды `BILLBOARD_OFFLINE`, `QUOTA_EXCEEDS_CAPACITY`, `INVALID_*` относятся к
внутренней библиотеке `Billboard.Scheduling`; в API P3 используется его
собственный `CapacityScheduleValidator`.

### 2.3. Назначение расписания группе

P3 уже отдаёт CRUD групп (`/api/v1/billboard-groups` и админские пути). Для
назначения одного расписания группе нужен отдельный сценарный endpoint; логику
проверки даёт P2 `GroupPlacementService`.

```http
POST /api/v1/placement-groups/validate
Authorization: Bearer <user token>
```

```json
{
  "billboardIds": [
    "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
    "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa"
  ],
  "template": {
    "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
    "startDate": "2026-11-10",
    "endDate": "2026-11-14",
    "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
    "startTime": "09:00:00",
    "endTime": "18:00:00",
    "minimumPlaysPerHour": 6,
    "videoDurationSeconds": 15
  }
}
```

Ответ — `GroupScheduleValidationResult`:

```json
{
  "canPublish": false,
  "requestedBillboardCount": 2,
  "availableBillboardCount": 1,
  "results": [
    {
      "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
      "billboardName": "Билборд №1",
      "validation": { "isAvailable": true, "conflicts": [], "alternatives": [] }
    },
    {
      "billboardId": "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa",
      "billboardName": "Билборд №2",
      "validation": {
        "isAvailable": false,
        "conflicts": [{
          "code": "INSUFFICIENT_ADVERTISING_CAPACITY",
          "message": "Недостаточно рекламной ёмкости в 10:00–11:00."
        }],
        "alternatives": []
      }
    }
  ]
}
```

Создание черновиков (все или ничего):

```http
POST /api/v1/placement-groups/drafts
Authorization: Bearer <user token>
```

Тело то же; ответ:

```json
{
  "success": true,
  "placements": [
    { "id": "...", "billboardId": "...", "status": "Draft" }
  ],
  "validation": { "canPublish": true }
}
```

Требования к P3:

- вызывать `GroupPlacementService.ValidateAsync` / `CreateDraftsAsync`;
- выполнять создание в одной транзакции с проверкой и резервированием;
- не публиковать группу частично, если хотя бы одно устройство не прошло;
- для «Моих групп» нужен владелец группы и фильтрация; сейчас `BillboardGroup`
  в P3 не содержит `OwnerId`, это отдельное решение P3/P1.

P1 показывает `results` по устройствам и не даёт опубликовать, пока
`canPublish = false`.

## 3. Интеграционные требования

### P3 (сделано)

1. `ManifestFileDto` содержит `placementId` и `minimumPlaysPerHour`;
   `DeviceService` отдаёт отдельную запись на каждое размещение.
2. API групп билбордов добавлен:
   - `GET /api/v1/billboard-groups`, `GET /api/v1/billboard-groups/{id}` — клиент;
   - `GET/POST/PATCH/DELETE /api/v1/admin/billboard-groups` — админ;
   - `POST/DELETE /api/v1/admin/billboard-groups/{id}/billboards` — состав группы.
3. Осталось только возможное объединение `CapacityScheduleValidator` с
   `Billboard.Scheduling`, чтобы не поддерживать две реализации алгоритма.

### P1 (интерфейс)

1. Использовать `Authorization: Bearer` для пользовательских запросов P3.
2. Для валидации — `POST /placements/validate`; показывать `conflicts[].message`
   и кнопки `alternatives[].startAt/endAt`.
3. Не считать пересечение интервалов конфликтом: решение принимает
   `available`.
4. Токен устройства в браузере не хранить; его получает только симулятор.

### P2 (сделано)

- `X-Device-Token` в каждом запросе устройства;
- P3 manifest/heartbeat/events/cache-reports DTO;
- API по умолчанию `http://localhost:8080`;
- ответ событий P3 — счётчики, outbox очищается по сумме счётчиков;
- `placementId` и `minimumPlaysPerHour` читаются из manifest;
- один ролик в нескольких размещениях: файл кэшируется один раз, ротация и
  события ведутся отдельно по каждому `placementId`.

## 4. Примеры

```bash
# manifest
curl -H "X-Device-Token: <billboardId>.<secret>" \
  http://localhost:8080/api/v1/devices/<billboardId>/manifest

# heartbeat
curl -X POST http://localhost:8080/api/v1/devices/<billboardId>/heartbeat \
  -H "X-Device-Token: <billboardId>.<secret>" \
  -H "Content-Type: application/json" \
  -d '{"simulatorVersion":"1.0.0","status":"Online"}'

# события
curl -X POST http://localhost:8080/api/v1/devices/<billboardId>/playback-events \
  -H "X-Device-Token: <billboardId>.<secret>" \
  -H "Content-Type: application/json" \
  -d '{"events":[{"idempotencyKey":"key-1","placementId":"...","videoAssetId":"...","startedAt":"2026-11-09T12:00:10Z","durationSeconds":15,"result":"Completed"}]}'
```
