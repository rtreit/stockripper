FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY fsharp-app.fsproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .

ENTRYPOINT ["dotnet", "fsharp-app.dll"]
