# Участник 2: расписания и симулятор

Документ описывает модуль, который можно разрабатывать и проверять независимо от
интерфейса и backend. Работа выполнена в ветке `feature/p2-scheduling-simulator`,
изменения готовятся к Pull Request в `develop`.

## 1. Что реализовано

| Задача из ТЗ | Где | Проверка |
|---|---|---|
| Модели расписаний, групп и размещений | `src/Billboard.Scheduling/Models` | `ScheduleWindowTests`, `PlacementServiceTests` |
| Проверка доступности и коллизий | `Schedules/ScheduleValidator.cs`, `CapacitySnapshot.cs` | `ScheduleValidatorTests`, `CapacitySnapshotTests` |
| Поиск альтернативного времени | `Schedules/AlternativeSlotFinder.cs` | `ScheduleValidatorTests.Validate_AlternativeSlot_IsActuallyAvailable` |
| Квоты показов и рекламная ёмкость | `Schedules/CapacitySnapshot.cs` | `CapacitySnapshotTests` |
| Очередь воспроизведения | `Playback/PlaybackQueueBuilder.cs` | `PlaybackQueueBuilderTests` |
| Симулятор на C# с локальным кэшем | `src/Billboard.Simulator` | `SimulatorRunnerTests` |
| Загрузка по manifest, ротация, офлайн | `Api`, `Runtime`, `Cache` | `ManifestSourcesTests`, `PlaybackRotatorTests`, `SimulatorRunnerTests` |
| Heartbeat и события показов | `Api/SimulatorApi.cs`, `Runtime/SimulatorRunner.cs` | `SimulatorRunnerTests` |
| Модульные тесты алгоритмов | `tests/Billboard.Scheduling.Tests` | 69 тестов |
| Тесты симулятора | `tests/Billboard.Simulator.Tests` | 39 тестов |

Дополнительно сделаны импорт/экспорт расписания в JSON и восстановление
неотправленных событий после перезапуска симулятора.

## 2. Быстрый старт

Требуется .NET SDK 10.0: все проекты решения, включая P2, нацелены на `net10.0`.

```powershell
cd I:\TEXNOPROG\Billboards-logic

dotnet restore BillboardSystem.P2.sln
dotnet build BillboardSystem.P2.sln
dotnet test  BillboardSystem.P2.sln
```

`BillboardSystem.P2.sln` — отдельное локальное решение только с проектами
участника 2. Общий `BillboardSystem.slnx` не изменялся, чтобы не мешать
участникам 1 и 3.

Демонстрация без backend:

```powershell
dotnet run --project src/Billboard.Simulator -- --demo
```

Демо показывает:

1. загрузку manifest и кэширование трёх роликов;
2. ротацию по квотам;
3. обрыв связи и накопление событий в outbox;
4. восстановление связи и отправку накопленных событий;
5. лимит кэша и удаление только неиспользуемого ролика.

Пример вывода:

```text
--- 2. Ротация по квотам (онлайн) ---
  PLAY demo-city.mp4 (15 с) → событие отправлено
  PLAY demo-mall.mp4 (20 с) → событие отправлено
  PLAY demo-metro.mp4 (10 с) → событие отправлено
  PLAY demo-city.mp4 (15 с) → событие отправлено

--- 3. Обрыв связи: показ из кэша, события в outbox ---
  PLAY demo-city.mp4 → сети нет, событие в outbox (1)
  PLAY demo-mall.mp4 → сети нет, событие в outbox (2)

--- 5. Лимит кэша и защита активного контента ---
  Все файлы активны → CACHE_CAPACITY_EXCEEDED: ...
  Освобождено 4 МБ (удалён только неактивный ролик)
  OK  demo-extra.mp4 — 5 МБ
```

Запуск против API:

```powershell
dotnet run --project src/Billboard.Simulator -- `
  --billboard-id d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321 `
  --api http://localhost:5000 `
  --cache simulator-cache
```

Параметры и переменные окружения:

| Параметр | Переменная | По умолчанию |
|---|---|---|
| `--billboard-id` | `BILLBOARD_ID` | обязателен (кроме `--demo`) |
| `--api` | `BILLBOARD_API_URL` | `http://localhost:5000` |
| `--cache` | `BILLBOARD_CACHE_DIR` | `simulator-cache` |
| `--cache-mb` | `BILLBOARD_CACHE_MB` | 32768 (32 ГБ) |
| `--heartbeat` | — | 30 секунд |
| `--refresh` | — | 60 секунд |
| `--batch` | — | 20 событий |
| `--speed` | `BILLBOARD_SPEED` | 1.0 |
| `--max-plays` | — | 0 (без ограничения) |
| `--manifest` | — | работать по локальному JSON без API |
| `--demo` | `BILLBOARD_DEMO=true` | автономная демонстрация |

Остановка — `Ctrl+C`. Симулятор дописывает outbox на диск и завершает heartbeat.

## 3. Структура

```text
src/
├── Billboard.Scheduling/          # чистая библиотека, без внешних зависимостей
│   ├── Models/                    # Billboard, Placement, статусы, результаты
│   ├── Schedules/                 # часы, ёмкость, валидатор, альтернативы
│   ├── Management/                # репозитории, PlacementService
│   ├── Playback/                  # manifest, события, очередь на час
│   └── Serialization/             # импорт/экспорт расписания
│
└── Billboard.Simulator/           # консольное приложение устройства
    ├── Api/                       # HTTP-клиент, источники manifest
    ├── Cache/                     # локальный кэш 32 ГБ, загрузчики
    ├── Runtime/                   # ротация, outbox, основной цикл
    └── DemoScenario.cs            # автономная демонстрация

tests/
├── Billboard.Scheduling.Tests/    # алгоритмы расписаний
└── Billboard.Simulator.Tests/     # кэш, outbox, ротация, API, runner
```

`Billboard.Scheduling` не ссылается на `Billboard.Domain` и `Billboard.Application`.
Это сделано специально: модуль можно проверять и развивать, пока участник 3
согласовывает EF Core-сущности. При интеграции модели маппятся на доменные
сущности, а сервисы подключаются к API.

## 4. Модель расписания

`Placement` хранит:

- `BillboardId`, `VideoAssetId`, `OwnerId`;
- `StartDate`/`EndDate` — даты включительно;
- `DaysOfWeek` — дни недели;
- `StartTime`/`EndTime` — окно внутри дня в локальном времени билборда;
- `MinimumPlaysPerHour` — минимальная квота показов в каждый час окна;
- `VideoDurationSeconds`;
- `Status`, `EstimatedCost`, `ReservedAmount`, `Version`.

Окно, у которого `EndTime < StartTime`, переходит через полночь. Например,
понедельник 22:00–02:00 занимает часы 22 и 23 понедельника и 0, 1 вторника.
Если это последний день расписания, «хвост» после полуночи не учитывается:
`EndDate` — последняя дата, когда окно может начаться.

`DaysOfWeekMask` (для БД) использует бит 0 для воскресенья, бит 1 для
понедельника и т. д. — как `System.DayOfWeek`.

### Статусы

Ёмкость занимают только `Scheduled`, `Downloading`, `Ready`, `Active`.
`Draft`, `PendingPayment`, `Paused`, `Completed`, `Cancelled`, `Failed`
ёмкость не занимают.

Переходы, которые выполняет `PlacementService`:

```text
Draft/PendingPayment/Failed --publish--> Scheduled
Scheduled/Downloading/Ready/Active --pause--> Paused
Paused --resume--> прежний статус (Scheduled/.../Active)
любой нетерминальный --cancel--> Cancelled
Scheduled/.../Paused --complete--> Completed
```

`Publish`, `UpdateSchedule` и `Resume` повторно проверяют доступность.
`CreateDraft` проверяет только форму расписания, чтобы черновик можно было
сохранить до оплаты.

## 5. Алгоритм проверки

### 5.1. Часовые слоты

Ёмкость считается по календарным часам. Слот `09:00–10:00` — это `Hour = 9`.
Если окно начинается или заканчивается внутри часа, час считается занятым
целиком — это консервативно и не пропускает перегрузку.

`ScheduleWindow.GetBuckets` разворачивает размещение в набор пар
`(DateOnly date, int hour)` с учётом дат, дней недели и перехода через полночь.
Повторы внутри одного размещения удаляются.

### 5.2. Требуемая ёмкость

```text
requiredSecondsPerHour = MinimumPlaysPerHour * VideoDurationSeconds
```

Для каждого слота суммируются требования всех размещений, которые его занимают,
плюс проверяемый кандидат. Если сумма больше
`Billboard.AdvertCapacitySecondsPerHour`, это конфликт.

Пример из ТЗ: 6 показов/ч × 15 с = 90 с/ч. При ёмкости 3600 с/ч остаётся
3510 с/ч для других размещений.

### 5.3. Конфликты

| Код | Причина |
|---|---|
| `INVALID_DATE_RANGE` | `StartDate > EndDate` |
| `DATE_RANGE_TOO_LONG` | расписание длиннее `MaxHorizonDays` (366) |
| `EMPTY_DAYS_OF_WEEK` | не выбран ни один день |
| `INVALID_TIME_WINDOW` | начало и конец окна совпадают |
| `INVALID_DURATION` | длительность ролика ≤ 0 |
| `INVALID_PLAYS_PER_HOUR` | квота ≤ 0 |
| `INVALID_COST` | отрицательная стоимость |
| `INVALID_BILLBOARD_CAPACITY` | ёмкость билборда вне 1..3600 |
| `BILLBOARD_OFFLINE` | билборд не в состоянии `Online` |
| `QUOTA_EXCEEDS_CAPACITY` | квота одного размещения больше ёмкости часа |
| `INSUFFICIENT_ADVERTISING_CAPACITY` | суммарная ёмкость в часе превышена |
| `INSUFFICIENT_BUDGET` | `EstimatedCost > AvailableBudget` |
| `BILLBOARD_NOT_FOUND` | билборд группы не найден |

Конфликты ёмкости агрегируются по часу суток: в одном конфликте указываются
час, максимальное требование, доступная ёмкость, примеры дат и до 10
блокирующих размещений. Так API не возвращает сотни почти одинаковых ошибок.

### 5.4. Альтернативы

`AlternativeSlotFinder` перебирает начало окна с шагом 30 минут (настраивается),
сохраняя длительность, даты и дни недели. Если за текущий период места нет,
расписание сдвигается на следующую неделю (до 2 недель вперёд). Варианты
сортируются по близости к запрошенному времени и свободной ёмкости.

Альтернативы не предлагаются, если:

- конфликт вызван бюджетом или офлайном билборда;
- квота одного размещения больше ёмкости часа (временем это не лечится);
- `SuggestAlternatives = false`.

### 5.5. Группы

`ScheduleValidator.ValidateGroup` проверяет каждый билборд отдельно и возвращает
`GroupScheduleValidationResult` с результатами по устройствам. `CanPublish`
равен `true` только если прошли все. Частичная публикация запрещена: интерфейс
должен показать список проблемных устройств.

### 5.6. Очередь на час

`PlaybackQueueBuilder` строит план на календарный час:

- каждое активное размещение получает минимум `MinimumPlaysPerHour` показов;
- ролики перемешиваются stride-планировщиком пропорционально квотам;
- старты считаются по фактическим длительностям;
- если суммарная длительность превышает ёмкость, план обрезается и возвращает
  предупреждения `PLAYBACK_CAPACITY_EXCEEDED` / `PLAYBACK_PLAN_TRUNCATED`.

Симулятор использует ту же идею для ротации файлов из manifest.

## 6. Симулятор

### 6.1. Цикл

```text
старт
  ├─ heartbeat каждые N секунд
  └─ цикл:
       ├─ получить manifest (API или локальный файл)
       ├─ сохранить последнюю версию для офлайна
       ├─ догрузить отсутствующие файлы в кэш
       ├─ построить ротацию из готовых файлов
       ├─ показать ролик
       ├─ записать событие в outbox
       ├─ отправить пакет событий
       └─ повторить
```

При недоступности API:

- показ продолжается из уже загруженного кэша;
- события остаются в `outbox.json`;
- heartbeat помечает устройство офлайн;
- после восстановления связи outbox отправляется с теми же `IdempotencyKey`.

### 6.2. Кэш

`LocalCacheManager`:

- лимит по умолчанию 32 ГБ (настраивается);
- файл качается во временный `.part`, проверяется размер и SHA-256, затем
  атомарно перемещается в кэш;
- индекс `cache-index.json` переживает перезапуск;
- при нехватке места удаляются только файлы, не входящие в текущий manifest
  (LRU по `LastUsedUtc`);
- если все файлы используются активными размещениями, возвращается
  `CACHE_CAPACITY_EXCEEDED`, ничего не удаляется;
- повреждённый файл перекачивается при следующем обращении (при загрузке
  индекс сбрасывает флаг `Verified` для записей с контрольной суммой).

### 6.3. Outbox

`PlaybackEventOutbox` хранит события на диске. `EventId` — ключ идемпотентности,
`IdempotencyKey` по умолчанию равен `EventId` в формате `N`. Повторная отправка
после сбоя использует тот же ключ, поэтому backend может безопасно отбросить
дубликат.

## 7. Контракты для участников 1 и 3

Подробные JSON-примеры — в [p2-api-contract.md](p2-api-contract.md). Кратко:

| Метод | Endpoint | Назначение |
|---|---|---|
| `GET` | `/api/v1/devices/{billboardId}/manifest` | список нужных файлов |
| `POST` | `/api/v1/devices/{billboardId}/heartbeat` | состояние устройства |
| `POST` | `/api/v1/devices/{billboardId}/playback-events` | пакет показов |
| `POST` | `/api/v1/devices/{billboardId}/cache-reports` | отчёт о кэше |

Для интерфейса участнику 1 нужны:

- `POST /api/v1/placements/validate` → `ScheduleValidationResult`
  (конфликты, альтернативы, сводка ёмкости);
- `POST /api/v1/placements/{id}/publish` → та же проверка + сохранение;
- `GET /api/v1/placements/{id}/alternatives` → список альтернатив.

Для участника 3:

- реализовать `IPlacementRepository` и `IBillboardCatalog` на EF Core;
- вызывать `ScheduleValidator`/`PlacementService` внутри API, а не дублировать
  алгоритм;
- передавать `availableBudget` из кошелька в `PublishAsync`/`ResumeAsync`;
- сохранять `PlaybackEvent.IdempotencyKey` с уникальным индексом;
- при отмене размещения освобождать резерв (это финансовая часть, не P2).

Маппинг `Placement` на `AdPlacement` из ТЗ:

| `Placement` | `AdPlacement` |
|---|---|
| `StartDate` + `StartTime` | `StartAt` (локальное время билборда) |
| `EndDate` + `EndTime` | `EndAt` |
| `DaysOfWeek` | `DaysOfWeekMask` |
| `MinimumPlaysPerHour` | `MinimumPlaysPerHour` |
| `VideoDurationSeconds` | длительность из `VideoAsset` |
| `Status` | `PlacementStatus` |
| `ReservedAmount` | `ReservedAmount` |

`Placement.StatusBeforePause` — дополнительное поле, нужное для корректного
возобновления. Его можно хранить в `AdPlacement` или вычислять по истории.

## 8. Импорт и экспорт

`ScheduleSerializer` экспортирует `ScheduleExport`:

```json
{
  "schemaVersion": 1,
  "exportedAt": "2026-11-03T12:00:00+00:00",
  "placements": [ ... ],
  "groups": [ ... ]
}
```

Импорт проверяет версию схемы, уникальность Id, обязательные `BillboardId` и
`VideoAssetId`, даты, дни, окно, длительность и квоту. Занятость билбордов при
импорте не проверяется — для этого нужен каталог и `ScheduleValidator`.

## 9. Ограничения и допущения

- Время — локальное время билборда; пересчёт часовых поясов и DST не входит в
  P2 и должен выполняться на границе API.
- Частично занятый час считается занятым целиком (консервативно).
- Ёмкость задаётся секундами рекламы в час, по умолчанию 3600.
- `PlacementService` работает с in-memory репозиторием; EF Core — задача P3.
  Проверку и публикацию нужно выполнять в одной транзакции с блокировкой, иначе
  два клиента могут одновременно занять последнюю ёмкость.
- Бюджет проверяется только если передан `AvailableBudget`; резервирование
  денег — P3.
- HTTP-клиент симулятора ожидает, что `--api` указывает на корень хоста
  (`http://host:port`), а пути `/api/v1/...` добавляются самим клиентом.
- Авторизация устройства (`Authorization: Device <token>`) в клиенте пока не
  настроена: для локальной разработки API может быть анонимным, при включении
  auth нужно добавить заголовок в `HttpSimulatorApi`.
- Контрольные суммы в manifest опциональны; если их нет, проверяется размер.
- Проекты P2 target `net10.0`, как и остальные проекты решения. Для сборки
  нужен .NET SDK 10.0; на машине с одним SDK 9 проект не соберётся.

## 10. Проверка перед отправкой

```powershell
dotnet build BillboardSystem.P2.sln
dotnet test  BillboardSystem.P2.sln
dotnet run --project src/Billboard.Simulator -- --demo
```

Ожидается: сборка без ошибок, 108 тестов, успешное демо.

## 11. Git

Изменения подготовлены в ветке:

```text
feature/p2-scheduling-simulator
```

Pull Request создаётся в `develop` после проверки сборки и тестов.

Файлы, которые добавлены или изменены:

- `src/Billboard.Scheduling/**` — новый проект;
- `src/Billboard.Simulator/**` — заменён прототип на рабочую реализацию;
- `tests/Billboard.Scheduling.Tests/**`, `tests/Billboard.Simulator.Tests/**`;
- `BillboardSystem.slnx` — добавлены `Billboard.Scheduling` и тесты P2, чтобы CI собирал и запускал их;
- `BillboardSystem.P2.sln` — отдельное локальное решение для работы только над P2;
- `docs/p2-scheduling-and-simulator.md`, `docs/p2-api-contract.md`;
- `.gitignore` — добавлен `simulator-cache/`.

Перед отправкой проверьте `git status` и не добавляйте `bin/`, `obj/`,
`simulator-cache/`, локальные manifest и outbox.
