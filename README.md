# CommandLineMCP

## Configure authentication secrets

Telegram sign-in stores its short-lived OIDC state in server memory for up to
10 minutes. If the server restarts while a Telegram login is pending, that login
expires; start the sign-in again.

The server requires a persistent signing certificate and Telegram OIDC
credentials. Generate the initial file before starting Compose:

```powershell
New-Item -ItemType Directory -Force secrets | Out-Null
dotnet run --project mcp-server/CommandLineMcp.Admin -- auth init-secrets secrets/server.secrets.json
```

This requires the .NET 11 SDK. The command creates a self-signed RSA signing
certificate encoded as PFX base64 with a cryptographically random password. This
certificate signs/encrypts OAuth tokens and protects persisted data-protection
keys; it is not the public HTTPS/TLS certificate used by the reverse proxy. It
prints only the created path and refuses to overwrite an existing file. Keep the
generated file private, stable across restarts, and out of logs/backups shared
outside the service operator's control. Fill `Telegram.ClientId` and
`Telegram.ClientSecret` using the OIDC credentials BotFather provides for your
bot. The tracked `secrets/server.secrets.example.json` documents the shape, but
its certificate and credentials are placeholders.

The public issuer is `https://cmd.example.com/`; the MCP resource is
`https://cmd.example.com/mcp`. Compose mounts the secrets file read-only at
`/run/secrets/server.secrets.json`. Deployments can override the path with
`OAuth__SecretsFile`. Data protection keys persist at `/data/keys` on the
existing data bind mount.

For browser-based MCP clients, add each trusted HTTPS origin to
`Mcp:CorsAllowedOrigins` (for example `Mcp__CorsAllowedOrigins__0=https://chatgpt.com`).
The default is empty, which rejects cross-origin MCP requests. OAuth token and
revocation endpoints allow wildcard origins without credentialed cookies.

Configure the Telegram OIDC callback URL as
`https://cmd.example.com/auth/telegram/callback` in the BotFather-provided client
settings and allow the `cmd.example.com` domain. Add the MCP server in ChatGPT and
complete its OAuth flow; do not manually create or paste a ChatGPT client
secret. The authorization-server metadata is at
`https://cmd.example.com/.well-known/openid-configuration`; its dynamic client
registration endpoint is `https://cmd.example.com/register`. The server issues
authorization codes through `/connect/authorize`, tokens through `/connect/token`,
and revokes tokens through `/connect/revoke`. The MCP protected-resource
metadata is `https://cmd.example.com/.well-known/oauth-protected-resource/mcp` and
declares resource `https://cmd.example.com/mcp`. DCR creates the client credentials
as part of the OAuth setup; use the values returned by the registration flow.
The required scope is `commands`. Request `offline_access` as well if the client
needs refresh tokens; access tokens last 15 minutes and refresh tokens up to 30
days.

Telegram identities that have not been approved cannot sign in. New users found
during Telegram OIDC login are created with OAuth disabled and role 0. An
administrator can enable a Telegram ID with `users allow-oauth`; that command
also creates a missing user in the enabled, role 0 state.

## Run the server with Docker Compose

From the repository root, copy the published override template once, then start:

```powershell
Copy-Item compose.override.example.yaml compose.override.yaml
New-Item -ItemType Directory -Force data
docker compose up --build -d
Invoke-RestMethod http://localhost:8080/health
```

`compose.yaml` has the service, restart policy, database bind mount, and a
required read-only mount for `./secrets/server.secrets.json`. Generate that
file before starting Compose; a missing file fails startup rather than creating
an empty directory. The
ignored `compose.override.yaml` publishes port 8080; customize it locally as
needed. The tracked `compose.override.example.yaml` can be used to recreate it.

The server applies EF Core migrations at startup. SQLite is stored at
`./data/commandline-mcp.db` on the host through a bind mount. The database
directory is ignored by Git.

### Reverse proxy

Terminate public TLS at the deployment reverse proxy and forward
to the server over HTTP. Preserve the original host and scheme with `Host`,
`X-Forwarded-Host`, and `X-Forwarded-Proto: https`. Allow streaming responses
for MCP SSE and disable buffering/timeouts that cut off long responses. By
default, the app accepts forwarded headers only from explicitly trusted proxy
addresses. Set `ForwardedHeaders__KnownProxies__0` (and additional indexed entries
if needed) to the proxy source IP as seen by the server container. Store these
values in local ignored configuration. If the server and reverse proxy share a
Docker network whose membership is the intended trust boundary, opt in with
`ForwardedHeaders__TrustAllProxies=true`. This trusts every container able to
reach the server on that network to supply forwarded headers; keep that network
limited to trusted services. The setting defaults to `false`. The issuer stays fixed at
`https://cmd.example.com/`. Keep CORS limited to the ChatGPT origins needed by the
OAuth client and preserve the application's Content-Security-Policy; do not
replace either with wildcard proxy rules.

If the proxy-to-app connection is intentionally plain HTTP, set
`OAuth__AllowHttpTransport=true` in local ignored deployment configuration. This
opt-in disables HTTPS redirection and OpenIddict's transport security checks for
the internal hop; it does not change the HTTPS issuer, MCP resource, or Telegram
callback URL. Keep the server HTTP listener reachable only from the TLS proxy.
The option defaults to `false` and does not trust forwarded headers; configure
`ForwardedHeaders__KnownProxies` or the explicitly network-scoped
`ForwardedHeaders__TrustAllProxies` separately when forwarded headers are
needed.

## Admin CLI

The server image also contains `CommandLineMcp.Admin` at `/app/admin`. Run an
administrative command in the server container while it is online:

```powershell
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users allow-oauth TELEGRAM_ID
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users deny-oauth TELEGRAM_ID
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users set-role TELEGRAM_ID 1
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll users show TELEGRAM_ID
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll clients create TELEGRAM_ID workstation
docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll clients set-key TELEGRAM_ID CLIENT_ID
```

`clients create` requires an existing Telegram user and client name. It creates
an enabled client and prints its ID and a newly generated bearer key once; save
the key in the client's local configuration. `clients set-key` replaces a
client key and re-enables that client. Both commands check that the client
belongs to the specified user. To provide a replacement key without putting it
in command history or echoing it, pass it as one line on standard input:

```powershell
Get-Content -Raw .\new-client-key.txt | docker compose exec -T server dotnet /app/admin/CommandLineMcp.Admin.dll clients set-key TELEGRAM_ID CLIENT_ID --stdin
```

The supplied key must contain 32 through 4096 characters and no whitespace or
control characters. The server stores only its uppercase SHA-256 hash.

For an offline maintenance run, `run --no-deps` avoids starting or depending
on the server process. Stop the server first so SQLite has a single writer:

```powershell
docker compose stop server
docker compose run --rm --no-deps --entrypoint dotnet server /app/admin/CommandLineMcp.Admin.dll users show TELEGRAM_ID
docker compose start server
```

`allow-oauth` accepts one or more positive Telegram IDs from local administration and creates
missing
users with role 0. `deny-oauth` and `set-role` require all IDs to exist;
`set-role` accepts only 0 or 1. Duplicate IDs are ignored. Every batch is
atomic. The CLI applies EF migrations before executing a command, uses
`ConnectionStrings__Database` or the same `Data Source=data/commandline-mcp.db`
default as the server, and creates the database parent directory when needed.
Exit codes are 0 for success, 2 for invalid arguments, 3 for missing users,
and 1 for database errors. Use `--help` for command syntax.

## Create or apply migrations

The `CommandLineMcp.Data` project contains the DbContext, repositories, database
maintenance implementation, and design-time factory. Server endpoints, workers,
and the Admin CLI use the Data project through repository and maintenance
interfaces. EF Core queries and database operations stay inside
`CommandLineMcp.Data`; composition roots may register and configure the DbContext.
The interfaces return materialized domain objects and never expose `IQueryable`.
Repository behavior is covered by SQLite integration tests, while consumers can
be tested with mocked interfaces.
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
