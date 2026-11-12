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

## Client job API

The client API uses a per-client bearer key. The server stores only its
SHA-256 hexadecimal hash (uppercase is the documented form; verification also
accepts hashes stored with different hex casing). Send the original key in the
`Authorization: Bearer <key>` header. Never put it in URLs or logs.

Poll with `POST /api/client/jobs/poll` and JSON `{"waitSeconds":30}`. The wait
must be from 0 through 30 seconds. The server returns `204 No Content` when no
job is available, or `200 OK` with `jobId`, `command`, `workingDirectory`, and
`timeoutSeconds`. The oldest eligible pending job is claimed atomically and
persisted as `Running` before it is returned. A client may have only one
`Running` job; simultaneous polls do not claim another. Missing command or
working directory values are not eligible for execution.

Submit the result to `POST /api/client/jobs/{jobId}/result`:

```json
{"stdout":"...","stderr":"...","exitCode":0,"timedOut":false}
```

The server records `TimedOut` when `timedOut` is true, `Succeeded` for exit code
0, and `Failed` otherwise. An identical retry of a stored terminal result
returns `200`; a conflicting retry returns `409`. A result from another
client returns `404`. If a running job has no result after its timeout plus a
120-second grace period, a later poll marks it `Unknown`. Such jobs are never
requeued; a late result may still be submitted while the job is `Unknown`.

This API does not expose job creation or OAuth endpoints. Job creation and MCP
integration are separate server work.
