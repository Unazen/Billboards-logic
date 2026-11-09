# Сборка из корня репозитория: docker build -f src/Billboard.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Billboard.Domain/Billboard.Domain.csproj src/Billboard.Domain/
COPY src/Billboard.Application/Billboard.Application.csproj src/Billboard.Application/
COPY src/Billboard.Infrastructure/Billboard.Infrastructure.csproj src/Billboard.Infrastructure/
COPY src/Billboard.Api/Billboard.Api.csproj src/Billboard.Api/
RUN dotnet restore src/Billboard.Api/Billboard.Api.csproj
COPY src/ src/
RUN dotnet publish src/Billboard.Api/Billboard.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# ffmpeg нужен для проверки длительности (ffprobe) и сжатия видео.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Billboard.Api.dll"]
