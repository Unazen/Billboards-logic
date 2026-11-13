FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Billboard.Domain/Billboard.Domain.csproj src/Billboard.Domain/
COPY src/Billboard.Application/Billboard.Application.csproj src/Billboard.Application/
COPY src/Billboard.Web/Billboard.Web.csproj src/Billboard.Web/
RUN dotnet restore src/Billboard.Web/Billboard.Web.csproj
COPY src/ src/
RUN dotnet publish src/Billboard.Web/Billboard.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Billboard.Web.dll"]
