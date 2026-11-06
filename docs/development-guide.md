# Правила разработки

1. `main` содержит проверенную версию.
2. `develop` используется для интеграции.
3. Каждая задача выполняется в отдельной feature-ветке.
4. Изменения в `main` и `develop` вносятся через Pull Request.
5. Изменения API и общих моделей согласуются командой.
6. Нельзя коммитить пароли, токены и реальные секреты.
7. Каждый PR должен содержать описание изменений и способ проверки.
8. Изменения БД сопровождаются миграциями и проверкой сборки.

## Перед PR

    dotnet build BillboardSystem.slnx
    dotnet test BillboardSystem.slnx

Оба шага обязательны: юнит-тесты ловят арифметику и состояния, сквозные тесты —
регистрацию, вход, публикацию и списания по HTTP.

## Правила кода

- Никаких `double` для денег: только `decimal` (точность `18,4`).
- Никаких `DateTime.Now`: только `DateTimeOffset.UtcNow`; локальное время билборда —
  через `TimeZones`/`Billboard.TimeZoneId`.
- Владелец объекта берётся из токена, а не из тела запроса.
- Ожидаемые ошибки — `AppException` с кодом; ответ API всегда `{ code, message, details }`.
- Валидация в DTO: атрибуты ставятся на параметр записи (`[Required]`, `[Range]`),
  **не** через `[property: ...]`. В .NET 10 MVC падает с 500, если валидация навешана
  на свойство позиционной записи.
- Сравнение `DateTimeOffset` в LINQ to Entities допустимо только для PostgreSQL:
  SQLite хранит их в двоичном виде. В тестах такие фильтры делаются в памяти.
- Новые сервисы регистрируются в `Billboard.Infrastructure/DependencyInjection.cs`.
- Публичные контракты — в `Billboard.Application/Contracts`, реализация — в Infrastructure.

## Работа с БД

    dotnet ef migrations add <Name> -p src/Billboard.Infrastructure -s src/Billboard.Api
    dotnet ef database update -p src/Billboard.Infrastructure -s src/Billboard.Api

- Уникальные индексы обязательны для ключей идемпотентности и резервов.
- Удаление пользователей, видео и билбордов не должно ломать историю: `DeleteBehavior.Restrict`
  и мягкое удаление.
- `EnsureCreated` используется только в тестах; в PostgreSQL схема создаётся миграциями.

## Файлы

Исходники в репозитории — UTF-8 (без BOM). Не пересохраняйте `.cs` и `.md` через
Windows PowerShell 5.1: он читает UTF-8 как ANSI и портит кириллицу.
