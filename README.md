# BillboardSystem

Система управления рекламными билбордами.

## Технологии

- C# / ASP.NET Core Blazor
- ASP.NET Core Web API
- PostgreSQL / Entity Framework Core
- MinIO
- Docker Compose
- xUnit

## Запуск

Требуется установленный .NET SDK.

Сборка:
dotnet build BillboardSystem.sln

Тесты:
dotnet test BillboardSystem.sln

## Правила разработки

- Не изменять main напрямую.
- Не изменять develop напрямую.
- Работать в отдельных feature-ветках.
- Все изменения отправлять через Pull Request.
- Перед PR запускать dotnet build и dotnet test.

## Архитектура

Описание модулей находится в docs/.