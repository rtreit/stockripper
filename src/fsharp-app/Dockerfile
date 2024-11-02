FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY fsharp-app.fsproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 5001
ENV ASPNETCORE_URLS=http://*:5001
COPY --from=build /app/out .

ENTRYPOINT ["dotnet", "fsharp-app.dll"]
