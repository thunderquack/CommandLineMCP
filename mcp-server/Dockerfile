FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY CommandLineMCP.slnx ./
COPY Directory.Packages.props ./
COPY CommandLineMcp.Data/ ./CommandLineMcp.Data/
COPY CommandLineMcp.Server/ ./CommandLineMcp.Server/
COPY CommandLineMcp.Admin/ ./CommandLineMcp.Admin/
RUN dotnet restore CommandLineMcp.Server/CommandLineMcp.Server.csproj
RUN dotnet restore CommandLineMcp.Admin/CommandLineMcp.Admin.csproj
RUN dotnet publish CommandLineMcp.Server/CommandLineMcp.Server.csproj -c Release -o /app/publish --no-restore
RUN dotnet publish CommandLineMcp.Admin/CommandLineMcp.Admin.csproj -c Release -o /app/admin --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/admin /app/admin
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CommandLineMcp.Server.dll"]
