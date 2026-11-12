# CommandLineMCP client

The client polls the server, runs one PowerShell command at a time as the user
who started it, and retries completed results from a local SQLite journal. It
uses only the Python standard library.

## Setup

Python 3.14 or newer is required. Copy `client.ini.example` to `client.ini`,
then set the server URL and the bearer key issued for this client:

```powershell
Copy-Item client.ini.example client.ini
notepad client.ini
python main.py --config client.ini
```

Keep the config file private. The bearer key is sent only to the configured
server origin; HTTP redirects are rejected so credentials cannot be forwarded
to another host. `state_path` defaults to `client-state.sqlite3` beside the
configuration file. Back up that file while the client is stopped if its
pending results must survive disk loss.

Example configuration:

```ini
[client]
server_url = https://your-server.example
bearer_key = issued-client-bearer-key
state_path = client-state.sqlite3
shell = powershell
```

## Execution and recovery

The client stores each received job before execution, marks it started before
launching PowerShell, then durably stores stdout, stderr, exit code, and timeout
status before uploading. A completed result is retried on later connections;
the server's `200` or idempotent `409` response ends that retry. A job found in
the `started` state after a crash is treated as unknown and is never executed
again. The client leaves that state for server-side expiry or operator review.
An upload `409` means the server already has a different result: the client logs
an error and retains the result in a local `conflict` state for review without
retrying it or treating it as acknowledged. Only one process can use the same
state file at a time.

PowerShell is launched non-interactively with Unicode command encoding. Commands
run with the client's full user permissions and are not filtered. On timeout,
the client terminates the process tree on Windows and returns the output
collected so far with `timedOut = true` and a null exit code. `shells.Shell` is
the extension point for future `cmd` and Linux shell adapters.

The HTTP API contract currently used is:

- `POST /api/client/jobs/poll` with `{"waitSeconds":30}`; `204` means no job,
  `200` returns `{ "jobId", "command", "workingDirectory", "timeoutSeconds" }`.
- `POST /api/client/jobs/{jobId}/result` with stdout, stderr, nullable exitCode,
  and timedOut; only `200` acknowledges the result, while `409` blocks it for
  review.

## Tests

Run the local unit tests with:

```powershell
python -m unittest discover -s tests -v
```

The PowerShell integration test runs only on Windows. All other tests use a
temporary local SQLite database and fake API; they make no network requests.
