# CommandLineMCP

## Run the server with Docker Compose

From the repository root, copy the published override template once, then start:

```powershell
Copy-Item compose.override.example.yaml compose.override.yaml
New-Item -ItemType Directory -Force data
docker compose up --build -d
Invoke-RestMethod http://localhost:8080/health
```

`compose.yaml` has the service, restart policy, and database bind mount. The
ignored `compose.override.yaml` publishes port 8080; customize it locally as
needed. The tracked `compose.override.example.yaml` can be used to recreate it.

The server applies EF Core migrations at startup. SQLite is stored at
`./data/commandline-mcp.db` on the host through a bind mount. The database
directory is ignored by Git.

## Create or apply migrations

The `CommandLineMcp.Data` project contains the DbContext and design-time factory.
From `mcp-server`:

```powershell
dotnet tool install --global dotnet-ef --version 11.0.0
dotnet ef migrations add <MigrationName> --project CommandLineMcp.Data --startup-project CommandLineMcp.Data
dotnet ef database update --project CommandLineMcp.Data --startup-project CommandLineMcp.Data
```

Set `ConnectionStrings__Database` to override the default
`Data Source=data/commandline-mcp.db` connection string.
