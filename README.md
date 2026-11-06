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
dotnet build BillboardSystem.slnx

Тесты:
dotnet test BillboardSystem.slnx

## Правила разработки

- Не изменять main напрямую.
- Не изменять develop напрямую.
- Работать в отдельных feature-ветках.
- Все изменения отправлять через Pull Request.
- Перед PR запускать dotnet build и dotnet test.

## Архитектура

Описание модулей находится в docs/.

## Запуск через Docker Compose

    cp .env.example .env      # задайте пароли
    docker compose up --build

API: http://localhost:8080, консоль MinIO: http://localhost:9001.
Администратор создаётся из SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD.

## Миграции EF Core

    dotnet tool install --global dotnet-ef
    dotnet ef migrations add Initial -p src/Billboard.Infrastructure -s src/Billboard.Api

Пока миграций нет, схема создаётся автоматически (EnsureCreated).
