# P2 API contract: устройства и расписания

Контракт фиксирует формат обмена между симулятором, backend и интерфейсом.
Базовый путь — `/api/v1`. Все JSON-поля — `camelCase`, перечисления — строками.
Даты — `yyyy-MM-dd`, время — `HH:mm:ss`, моменты времени — ISO 8601 с offset.

Этот файл дополняет общий `docs/api-contracts.md`, не заменяя его. Если backend
меняет поля, нужно обновить и этот документ, и DTO в
`src/Billboard.Scheduling/Playback/DeviceContracts.cs`.

## 1. Устройства

### 1.1. Manifest

```http
GET /api/v1/devices/{billboardId}/manifest
Authorization: Device <token>
```

Ответ `200 OK`:

```json
{
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "version": "2026-11-03T12:00:00Z:42",
  "generatedAt": "2026-11-03T12:00:00+00:00",
  "cacheLimitBytes": 34359738368,
  "items": [
    {
      "placementId": "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa",
      "videoId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "fileName": "promo-42.mp4",
      "downloadUrl": "https://storage.example/api/v1/videos/2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122/download?token=...",
      "sizeBytes": 104857600,
      "durationSeconds": 15,
      "checksumSha256": "9f2c...",
      "playsPerHour": 6,
      "activeFrom": "2026-11-03T09:00:00+03:00",
      "activeUntil": "2026-11-03T18:00:00+03:00"
    }
  ]
}
```

Правила:

- отдавать только ролики, которые нужны устройству сейчас или в ближайшее
  время; не всю медиатеку;
- `version` меняется при любом изменении состава или расписания;
- `sizeBytes` обязателен и должен совпадать с фактическим размером;
- `checksumSha256` опционален, но рекомендуется; без него проверяется только
  размер;
- `downloadUrl` — временная ссылка или endpoint, доступный устройству;
- `durationSeconds` и `playsPerHour` используются ротацией;
- `activeFrom`/`activeUntil` позволяют симулятору не показывать просроченный
  ролик, даже если manifest устарел.

Если API недоступен, симулятор использует последний сохранённый manifest и
продолжает показ из кэша.

### 1.2. Heartbeat

```http
POST /api/v1/devices/{billboardId}/heartbeat
Content-Type: application/json
```

```json
{
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "reportedAt": "2026-11-03T12:00:05+00:00",
  "status": "Online",
  "simulatorVersion": "1.0.0",
  "cacheUsedBytes": 188743680,
  "cacheCapacityBytes": 34359738368,
  "cachedVideoCount": 3,
  "activePlacementCount": 3
}
```

Ответ `200 OK`:

```json
{
  "serverTime": "2026-11-03T12:00:06+00:00",
  "manifestVersion": "2026-11-03T12:00:00Z:42",
  "shouldRefreshManifest": false
}
```

Backend должен обновить `Billboard.LastSeenAt` и статус устройства. Если
heartbeat не проходит, симулятор переходит в офлайн-режим, но показ не
останавливает.

### 1.3. События показов

```http
POST /api/v1/devices/{billboardId}/playback-events
Content-Type: application/json
```

```json
{
  "events": [
    {
      "eventId": "5f0c2c1e-2222-4a3b-9a10-bbbbbbbbbbbb",
      "idempotencyKey": "5f0c2c1e22224a3b9a10bbbbbbbbbbbb",
      "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
      "placementId": "0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa",
      "videoId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "startedAt": "2026-11-03T12:00:10+00:00",
      "durationSeconds": 15,
      "result": "Success"
    }
  ]
}
```

Ответ `200 OK`:

```json
{
  "acceptedEventIds": ["5f0c2c1e-2222-4a3b-9a10-bbbbbbbbbbbb"],
  "duplicateEventIds": [],
  "rejected": []
}
```

Правила:

- `idempotencyKey` уникален; повторная отправка не должна создавать второй
  показ и второе списание;
- backend хранит уникальный индекс по `IdempotencyKey` (или `EventId`);
- дубликат возвращается в `duplicateEventIds`, а не считается ошибкой;
- `rejected` — постоянные ошибки (например, размещение отменено); симулятор
  удаляет такие события из outbox, чтобы не зациклиться;
- допустим ответ `204 No Content`; симулятор считает весь пакет принятым;
- желательно принимать пакет до 20 событий за запрос;
- финансовое списание выполняется ровно один раз в транзакции с вставкой
  события (участник 3).

### 1.4. Отчёт о кэше

```http
POST /api/v1/devices/{billboardId}/cache-reports
```

```json
{
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "reportedAt": "2026-11-03T12:00:06+00:00",
  "items": [
    {
      "videoId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
      "sizeBytes": 104857600,
      "checksumSha256": "9f2c...",
      "state": "Ready"
    }
  ]
}
```

`state`: `Ready`, `Missing`, `Downloading`, `Failed`. Отчёт нужен для
административной панели и диагностики; симулятор не блокируется, если отчёт не
дошёл.

## 2. Проверка расписания для интерфейса

```http
POST /api/v1/placements/validate
Content-Type: application/json
```

```json
{
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
  "startDate": "2026-11-02",
  "endDate": "2026-11-06",
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  "startTime": "09:00:00",
  "endTime": "18:00:00",
  "minimumPlaysPerHour": 6,
  "videoDurationSeconds": 15,
  "estimatedCost": 1200,
  "availableBudget": 5000
}
```

Ответ:

```json
{
  "isAvailable": false,
  "conflicts": [
    {
      "code": "INSUFFICIENT_ADVERTISING_CAPACITY",
      "message": "Недостаточно рекламной ёмкости в 10:00–11:00: требуется 3800 с/ч, доступно 3600 с/ч.",
      "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
      "hour": 10,
      "requiredSeconds": 3800,
      "availableSeconds": 3600,
      "dates": ["2026-11-02", "2026-11-03"],
      "blockingPlacementIds": ["7a1c..."]
    }
  ],
  "alternatives": [
    {
      "startDate": "2026-11-02",
      "endDate": "2026-11-06",
      "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "startTime": "12:00:00",
      "endTime": "21:00:00",
      "freeSecondsPerHour": 300,
      "peakUtilizationPercent": 91.7
    }
  ],
  "summary": {
    "capacitySecondsPerHour": 3600,
    "candidateSecondsPerHour": 300,
    "peakRequiredSecondsPerHour": 3800,
    "peakExistingSecondsPerHour": 3500,
    "applicableHourBuckets": 45,
    "overflowBuckets": 10,
    "peakUtilizationPercent": 105.6,
    "freeSecondsPerHour": -200,
    "overflowDetails": []
  }
}
```

`overflowDetails` можно не отдавать в UI или ограничить несколькими слотами:
массив предназначен для диагностики. `dates` уже ограничен 10 элементами,
`blockingPlacementIds` — тоже.

`POST /api/v1/placements/{id}/publish` возвращает тот же результат проверки и
публикует только при `isAvailable = true`. При конфликте — `409 Conflict` с
телом ошибки:

```json
{
  "code": "PLACEMENT_CONFLICT",
  "message": "Невозможно опубликовать размещение: недостаточно рекламного времени.",
  "details": {
    "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
    "conflicts": [ ... ]
  }
}
```

`GET /api/v1/placements/{id}/alternatives` возвращает только массив
`alternatives` в том же формате.

## 3. Идемпотентность

Обязательные ключи:

| Операция | Ключ |
|---|---|
| Показ | `PlaybackEvent.IdempotencyKey` |
| Пополнение | ключ запроса клиента (участник 3) |
| Списание/возврат | ключ, производный от события или размещения |

Требования к backend:

- уникальный индекс по ключу;
- вставка события и финансовые операции — в одной транзакции;
- повторный запрос возвращает прежний результат, а не создаёт новую операцию;
- для пакета событий дубликаты не считаются ошибкой.

## 4. Коды ошибок

| HTTP | Когда |
|---|---|
| `400` | невалидный JSON или поля запроса |
| `401` | нет токена устройства/пользователя |
| `403` | нет прав на билборд или размещение |
| `404` | билборд/размещение/видео не найдены |
| `409` | конфликт расписания или повтор операции с другим телом |
| `422` | семантически некорректное расписание (если используется) |
| `500` | внутренняя ошибка |

Формат ошибки:

```json
{
  "code": "PLACEMENT_CONFLICT",
  "message": "Понятное человеку сообщение.",
  "details": {}
}
```

## 5. Что должен сделать участник 3

1. Реализовать `IPlacementRepository` и `IBillboardCatalog` на EF Core.
2. В API вызвать `ScheduleValidator.Validate` для `validate` и
   `PlacementService.PublishAsync` для публикации; не дублировать алгоритм.
3. Передавать баланс кошелька в `PublishAsync(..., availableBudget)`.
4. Реализовать manifest: выбрать активные размещения билборда, проверить
   `VideoStatus.Ready`, сформировать временные ссылки.
5. Реализовать приём событий с уникальным индексом и транзакцией.
6. Обновлять `LastSeenAt` по heartbeat.
7. При отмене/завершении размещения освобождать резерв.

## 6. Что должен сделать участник 1

1. Показать `conflicts` понятными сообщениями, а `alternatives` — кнопками
   «выбрать это время».
2. Не считать любое пересечение времени конфликтом: конфликт определяет
   `isAvailable` и `summary`.
3. Показывать три пользовательских статуса: «Опубликовано», «Загрузка»,
   «Готово к показу», маппя внутренние статусы.
4. Для формы выбора времени передавать локальные дату/время билборда.
5. Не вызывать `publish` без успешного `validate`.

## 7. Пример `curl`

```bash
curl -X POST http://localhost:5000/api/v1/placements/validate \
  -H "Content-Type: application/json" \
  -d '{
    "billboardId":"d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
    "videoAssetId":"2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
    "startDate":"2026-11-02",
    "endDate":"2026-11-06",
    "daysOfWeek":["Monday","Tuesday","Wednesday","Thursday","Friday"],
    "startTime":"09:00:00",
    "endTime":"18:00:00",
    "minimumPlaysPerHour":6,
    "videoDurationSeconds":15
  }'
```
