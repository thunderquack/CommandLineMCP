# CommandLineMCP client

At startup, the client detects the operating system, CPU architecture and
available PowerShell 7, Windows PowerShell, Bash and sh executables. It
publishes this information through PUT /api/client/capabilities before polling.
The server exposes the published details through the MCP list_clients tool.
SHELL=auto picks pwsh on Windows, bash/sh on Linux. Existing
SHELL=powershell continues to select PowerShell 7 (pwsh).
The client continues operating against older servers without this endpoint.

The client polls the server, runs one PowerShell command at a time as the user
who started it, and retries completed results from a local SQLite journal.
Configuration is read from a local `.env` file with environment variables taking
precedence.

At startup, the client logs the selected shell and a sanitized server endpoint.
The client immediately reports that it is connecting. Its first poll waits at
most 2 seconds to confirm availability, while subsequent long polls wait up to
30 seconds for jobs. After the first successful poll it reports that it is
connected and waiting for jobs; connection loss and recovery are logged only when the state changes. Job
logs include the job ID, execution outcome, and result acknowledgement. Commands,
command output, and bearer keys are not included in normal status logs. With
DEBUG=true, commands and their stdout/stderr are printed to the console, but
bearer keys are not logged. Debug output may contain secrets. A completed
result that could not be uploaded is reported as pending retry.

## Setup

Python 3.14 or newer is required. Install the project dependency, copy
`.env.example` to `.env`, then set the bearer key issued for this client:

```powershell
python -m pip install -e .
Copy-Item .env.example .env
notepad .env
python main.py
```

Keep the config file private. The bearer key is sent only to the configured
server origin; HTTP redirects are rejected so credentials cannot be forwarded
to another host. `state_path` defaults to `client-state.sqlite3` beside the
configuration file. Back up that file while the client is stopped if its
pending results must survive disk loss.

Example configuration (`STATE_PATH` is relative to the config file directory):

```dotenv
SERVER_URL=https://cmd.example.com
BEARER_KEY=replace-with-issued-client-bearer-key
STATE_PATH=client-state.sqlite3
SHELL=auto
DEBUG=false
```

Press Ctrl+C to see a shutdown message. A running PowerShell process tree
is terminated; an interrupted job remains in started state (unknown outcome).

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

PowerShell 7 (pwsh.exe) is required and launched non-interactively with Unicode command encoding. Commands
run with the client's full user permissions and are not filtered. On timeout,
the client terminates the process tree on Windows and returns the output
collected so far with `timedOut = true` and a null exit code. `shells.Shell` is
the extension point for future `cmd` and Linux shell adapters.

The HTTP API contract currently used is:

- `POST /api/client/jobs/poll` with `{"waitSeconds":30}`; `204` means no job,
  `200` returns `{ "jobId", "command", "workingDirectory", "timeoutSeconds" }`;
  a null or omitted `workingDirectory` uses the client's current directory.
- `POST /api/client/jobs/{jobId}/result` with stdout, stderr, nullable exitCode,
  and timedOut; `200` acknowledges the result. A `409` means a conflicting
  result and is retained locally for review without retry.

## Tests

Run the local unit tests with:

```powershell
python -m unittest discover -s tests -v
```

The PowerShell integration test runs only on Windows. All other tests use a
temporary local SQLite database and fake API; they make no network requests.
