# API Contracts

Базовый путь: `/api/v1`. Все ответы — JSON (кроме скачивания файла).
Ошибки: `{ "code", "message", "details" }`; коды HTTP: 400 валидация, 401 нет токена,
403 нет прав, 404 не найдено (в том числе чужой объект), 409 конфликт состояния,
402 имитация отказа платежа, 500 внутренняя ошибка.

Авторизация:

- пользователь — `Authorization: Bearer <accessToken>` из `POST /account/login`;
- устройство — `X-Device-Token: <billboardId>.<secret>` (выдаётся один раз при создании
  билборда или ротации токена).

Идемпотентность:

- `POST /billing/top-ups`, `POST /billing/subscriptions` — обязательное поле `idempotencyKey`;
- `POST /devices/{id}/playback-events` — обязательный `idempotencyKey` у каждого события;
- повтор с тем же ключом не создаёт вторую операцию и не списывает деньги второй раз.

## Аккаунт

| Метод | Endpoint | Назначение |
|---|---|---|
| POST | `/api/v1/account/register` | Регистрация клиента (роль `Client`) |
| POST | `/api/v1/account/login` | Вход, в ответе `accessToken`, `expiresIn`, `refreshToken` |
| GET | `/api/v1/account/me` | Текущий пользователь, роли, действующий тариф |

## Тарифы

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/tariffs` | Публичный список активных тарифов |
| GET | `/api/v1/tariffs/current` | Действующий тариф пользователя |
| GET | `/api/v1/admin/tariffs` | Все тарифы (Admin) |
| POST | `/api/v1/admin/tariffs` | Создать тариф (Admin) |
| PATCH | `/api/v1/admin/tariffs/{id}` | Изменить лимиты (Admin) |

## Билборды

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/billboards` | Каталог доступных билбордов |
| GET | `/api/v1/billboards/{id}` | Детали билборда |
| GET | `/api/v1/admin/billboards` | Все билборды, включая отключённые (Admin) |
| POST | `/api/v1/admin/billboards` | Создать; в ответе `billboard` и `deviceToken` (Admin) |
| PATCH | `/api/v1/admin/billboards/{id}` | Изменить параметры (Admin) |
| POST | `/api/v1/admin/billboards/{id}/disable` | Отключить (Admin) |
| POST | `/api/v1/admin/billboards/{id}/rotate-device-token` | Новый токен устройства (Admin) |

## Группы билбордов

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/billboard-groups` | Список групп с составом (любой авторизованный) |
| GET | `/api/v1/billboard-groups/{id}` | Одна группа |
| GET | `/api/v1/admin/billboard-groups` | Список групп (Admin) |
| POST | `/api/v1/admin/billboard-groups` | Создать группу `{ "name": "Центр" }` (Admin) |
| PATCH | `/api/v1/admin/billboard-groups/{id}` | Переименовать (Admin) |
| DELETE | `/api/v1/admin/billboard-groups/{id}` | Удалить группу вместе с составом (Admin) |
| POST | `/api/v1/admin/billboard-groups/{id}/billboards` | Добавить билборды `{ "billboardIds": [...] }` (Admin) |
| DELETE | `/api/v1/admin/billboard-groups/{id}/billboards/{billboardId}` | Убрать билборд (Admin) |

Повторное добавление билборда игнорируется, неизвестный id — 400 `UNKNOWN_BILLBOARD`,
повтор названия — 409 `GROUP_NAME_EXISTS`. Групповое назначение размещений
с проверкой всех устройств выполняет планировщик участника 2.

## Видео

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/videos` | Медиатека: свои файлы; `?all=true` — все (Admin) |
| POST | `/api/v1/videos/uploads` | Шаг 1: задача загрузки, проверка формата и лимитов тарифа |
| PUT | `/api/v1/videos/{id}/content` | Шаг 2: тело запроса — файл (`application/octet-stream`) |
| POST | `/api/v1/videos/import-url` | Импорт по ссылке (защита от SSRF) |
| GET | `/api/v1/videos/{id}` | Метаданные и статус |
| GET | `/api/v1/videos/{id}/download` | Временная ссылка на оригинал |
| DELETE | `/api/v1/videos/{id}` | Мягкое удаление; 409 `VIDEO_IN_USE`, если используется |

Шаг 1 возвращает `{ videoId, status, maxSizeBytes }`. Файл проходит проверку размера,
формата, длительности (ffprobe), при необходимости сжимается (ffmpeg) и только после
проверки целостности получает статус `Ready`.

## Размещения

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/placements?status=&all=` | Список: свои; `all=true` — все (Admin) |
| GET | `/api/v1/placements/{id}` | Детали и статистика показов |
| POST | `/api/v1/placements` | Создать черновик |
| PATCH | `/api/v1/placements/{id}` | Изменить черновик или `PendingPayment` |
| POST | `/api/v1/placements/validate` | Проверка без сохранения: доступность, цена, альтернативы |
| GET | `/api/v1/placements/{id}/alternatives?max=3` | Ближайшие свободные окна |
| POST | `/api/v1/placements/{id}/publish` | Проверить, зарезервировать бюджет, перевести в `Scheduled` |
| POST | `/api/v1/placements/{id}/pause` | Пауза (`PauseReason = Manual`) |
| POST | `/api/v1/placements/{id}/resume` | Возобновление; без денег — `PendingPayment` |
| POST | `/api/v1/placements/{id}/cancel` | Отмена и возврат неиспользованного резерва |

Пример создания:

```json
{
  "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
  "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
  "startAt": "2026-11-02T00:00:00Z",
  "endAt": "2026-11-30T00:00:00Z",
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  "startTimeOfDay": "09:00:00",
  "endTimeOfDay": "18:00:00",
  "minimumPlaysPerHour": 6
}
```

Владелец размещения всегда берётся из токена: `ownerId` в запросе не принимается.

Ответ `POST /placements/validate`:

```json
{
  "available": false,
  "quote": { "totalAmount": 2.70, "expectedPlays": 54, "pricePerPlay": 0.05 },
  "conflicts": [{ "billboardId": "…", "reason": "INSUFFICIENT_ADVERTISING_CAPACITY", "message": "…" }],
  "alternatives": [{ "startAt": "2026-11-02T10:00:00Z", "endAt": "2026-11-02T19:00:00Z" }]
}
```

Публикация без денег возвращает 409 `INSUFFICIENT_FUNDS`, размещение остаётся
в `PendingPayment`, списаний нет.

## Финансы

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/billing/wallet` | Баланс и резерв |
| POST | `/api/v1/billing/top-ups` | Имитация пополнения (идемпотентно) |
| GET | `/api/v1/billing/transactions?skip=&take=` | История операций |
| GET | `/api/v1/billing/quote/{placementId}?billboardCount=` | Предварительный расчёт |
| POST | `/api/v1/billing/subscriptions` | Оформить подписку |
| POST | `/api/v1/billing/subscriptions/{id}/cancel` | Отменить подписку |
| GET | `/api/v1/billing/earnings?billboardId=&take=` | Доход владельца (Admin) |
| GET | `/api/v1/billing/usage?all=` | Показы и затраты по размещениям |

Типы операций: `TopUp`, `Reservation`, `Charge`, `Release`, `Refund`, `OwnerCredit`.

## Устройства (X-Device-Token)

| Метод | Endpoint | Назначение |
|---|---|---|
| POST | `/api/v1/devices/{id}/heartbeat` | Сигнал работоспособности, версия симулятора |
| GET | `/api/v1/devices/{id}/manifest` | Файлы для показа и временные ссылки |
| POST | `/api/v1/devices/{id}/cache-reports` | Что уже скачано и проверено |
| GET | `/api/v1/devices/{id}/configuration` | Лимит кэша (32 ГБ) и настройки |
| POST | `/api/v1/devices/{id}/playback-events` | Пакет событий показов (до 500) |

Манифест:

```json
{
  "billboardId": "…",
  "version": 1837462159,
  "generatedAt": "2026-11-06T18:00:00Z",
  "cacheLimitBytes": 34359738368,
  "cacheUsedBytes": 0,
  "files": [{
    "videoAssetId": "…",
    "placementId": "…",
    "minimumPlaysPerHour": 6,
    "fileName": "ad.mp4",
    "sizeBytes": 2048,
    "durationSeconds": 15,
    "checksum": "…",
    "downloadUrl": "http://minio:9000/billboard-videos/…",
    "urlExpiresAt": "2026-11-06T18:15:00Z"
  }]
}
```

Каждая запись `files[]` соответствует **размещению**, а не файлу: если один ролик
показывается по двум размещениям, в массиве будет две записи с одинаковым
`videoAssetId`, разными `placementId` и своими квотами. `placementId` нужен
симулятору, чтобы отправлять события показов, а `minimumPlaysPerHour` — чтобы
соблюдать квоту.

`version` меняется при любом изменении набора размещений — по нему устройство понимает,
что манифест устарел. Ссылки живут `Media:DownloadUrlTtlMinutes` (по умолчанию 15 минут).
Симулятор может кэшировать файл по `videoAssetId` и не скачивать его дважды.

## Администрирование

| Метод | Endpoint | Назначение |
|---|---|---|
| GET | `/api/v1/admin/users?skip=&take=` | Пользователи |
| PATCH | `/api/v1/admin/users/{id}/active` | Блокировка/разблокировка |
| PATCH | `/api/v1/admin/users/{id}/role` | Роль `Admin` или `Client` |
| GET | `/api/v1/admin/logs?action=&userId=&from=&to=&skip=&take=` | Журнал действий |
| GET | `/api/v1/admin/logs/export` | CSV (UTF-8 BOM, защита от формул Excel) |
| GET | `/api/v1/admin/devices` | Мониторинг устройств и кэша |

## Статусы размещения

`Draft → PendingPayment → Scheduled → Downloading → Ready → Active → Completed`,
а также `Paused` (ручная пауза или исчерпание бюджета) и `Cancelled`.
Публикация возможна из `Draft` и `PendingPayment`.
