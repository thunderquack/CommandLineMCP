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

## Admin CLI

The server image also contains `CommandLineMcp.Admin` at `/app/admin`. Run an
administrative command in the server container while it is online:

```powershell
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users allow-oauth 123456789
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users deny-oauth 123456789
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users set-role 123456789 1
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users show 123456789
```

For an offline maintenance run, `run --no-deps` avoids starting or depending
on the server process. Stop the server first so SQLite has a single writer:

```powershell
docker compose stop server
docker compose run --rm --no-deps --entrypoint dotnet server /app/admin/CommandLineMcp.Admin.dll users show 123456789
docker compose start server
```

`allow-oauth` accepts one or more positive Telegram IDs and creates missing
users with role 0. `deny-oauth` and `set-role` require all IDs to exist;
`set-role` accepts only 0 or 1. Duplicate IDs are ignored. Every batch is
atomic. The CLI applies EF migrations before executing a command, uses
`ConnectionStrings__Database` or the same `Data Source=data/commandline-mcp.db`
default as the server, and creates the database parent directory when needed.
Exit codes are 0 for success, 2 for invalid arguments, 3 for missing users,
and 1 for database errors. Use `--help` for command syntax.

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
