FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY CommandLineMCP.slnx ./
COPY CommandLineMcp.Data/ ./CommandLineMcp.Data/
COPY CommandLineMcp.Server/ ./CommandLineMcp.Server/
RUN dotnet restore CommandLineMCP.slnx
RUN dotnet publish CommandLineMcp.Server/CommandLineMcp.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CommandLineMcp.Server.dll"]
