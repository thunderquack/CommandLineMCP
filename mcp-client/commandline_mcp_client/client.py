from __future__ import annotations

import json
import logging
import os
import sqlite3
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol
from urllib.parse import urlsplit

from .shells import ExecutionResult, Shell, create_shell

LOG = logging.getLogger("commandline_mcp_client")


@dataclass(frozen=True)
class ClientConfig:
    server_url: str
    bearer_key: str
    state_path: Path
    shell: str = "powershell"
    request_timeout_seconds: float = 40


class HttpTransport(Protocol):
    def request(self, method: str, path: str, payload: dict | None = None) -> tuple[int, dict | None]: ...


class ApiError(Exception):
    pass


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Do not forward Authorization credentials to a redirect target."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class HttpApi:
    def __init__(self, server_url: str, bearer_key: str, timeout: float = 40):
        parsed = urlsplit(server_url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise ValueError("server_url must be an absolute HTTP(S) URL")
        self.base_url = server_url.rstrip("/")
        self.bearer_key = bearer_key
        self.timeout = timeout
        self.opener = urllib.request.build_opener(NoRedirectHandler())

    def request(self, method: str, path: str, payload: dict | None = None) -> tuple[int, dict | None]:
        body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            self.base_url + path,
            data=body,
            method=method,
            headers={
                "Authorization": f"Bearer {self.bearer_key}",
                "Accept": "application/json",
                **({"Content-Type": "application/json; charset=utf-8"} if body is not None else {}),
            },
        )
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                content = response.read()
                return response.status, json.loads(content.decode("utf-8")) if content else None
        except urllib.error.HTTPError as exc:
            content = exc.read()
            if exc.code in (204, 409):
                return exc.code, None
            # Avoid logging or exposing response bodies which could contain secrets.
            raise ApiError(f"server returned HTTP {exc.code}") from None
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise ApiError(f"request failed ({type(exc).__name__})") from None


class Journal:
    """Durable at-most-once execution journal, keyed by server job ID."""

    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path, timeout=30)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.execute(
            """CREATE TABLE IF NOT EXISTS jobs (
                job_id TEXT PRIMARY KEY,
                command TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                timeout_seconds REAL NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('received','started','completed','acknowledged','conflict')),
                stdout TEXT,
                stderr TEXT,
                exit_code INTEGER,
                timed_out INTEGER,
                received_at REAL NOT NULL,
                updated_at REAL NOT NULL
            )"""
        )
        self.db.commit()

    def close(self) -> None:
        self.db.close()

    def add_received(self, job: dict) -> bool:
        now = time.time()
        cursor = self.db.execute(
            "INSERT OR IGNORE INTO jobs(job_id,command,working_directory,timeout_seconds,state,received_at,updated_at) VALUES(?,?,?,?, 'received',?,?)",
            (job["jobId"], job["command"], job["workingDirectory"], job["timeoutSeconds"], now, now),
        )
        self.db.commit()
        return cursor.rowcount == 1

    def mark_started(self, job_id: str) -> None:
        self.db.execute("UPDATE jobs SET state='started',updated_at=? WHERE job_id=? AND state='received'", (time.time(), job_id))
        self.db.commit()

    def mark_completed(self, job_id: str, result: ExecutionResult) -> None:
        self.db.execute(
            "UPDATE jobs SET state='completed',stdout=?,stderr=?,exit_code=?,timed_out=?,updated_at=? WHERE job_id=? AND state='started'",
            (result.stdout, result.stderr, result.exit_code, int(result.timed_out), time.time(), job_id),
        )
        self.db.commit()

    def mark_acknowledged(self, job_id: str) -> None:
        self.db.execute("UPDATE jobs SET state='acknowledged',updated_at=? WHERE job_id=? AND state='completed'", (time.time(), job_id))
        self.db.commit()

    def mark_conflict(self, job_id: str) -> None:
        self.db.execute("UPDATE jobs SET state='conflict',updated_at=? WHERE job_id=? AND state='completed'", (time.time(), job_id))
        self.db.commit()

    def pending_results(self) -> list[sqlite3.Row]:
        return list(self.db.execute("SELECT * FROM jobs WHERE state='completed' ORDER BY received_at,job_id"))

    def state(self, job_id: str) -> str | None:
        row = self.db.execute("SELECT state FROM jobs WHERE job_id=?", (job_id,)).fetchone()
        return row["state"] if row else None


def _valid_job(job: object) -> dict:
    if not isinstance(job, dict):
        raise ApiError("server returned an invalid job")
    try:
        job_id = job["jobId"]
        command = job["command"]
        working_directory = job.get("workingDirectory")
        timeout = float(job["timeoutSeconds"])
        if not all(isinstance(v, str) and v for v in (job_id, command)):
            raise ValueError
        if working_directory is None or working_directory == "":
            working_directory = os.getcwd()
        if not isinstance(working_directory, str):
            raise ValueError
        if timeout <= 0:
            raise ValueError
    except (KeyError, TypeError, ValueError):
        raise ApiError("server returned an invalid job") from None
    return {"jobId": job_id, "command": command, "workingDirectory": working_directory, "timeoutSeconds": timeout}


class Runner:
    def __init__(self, api: HttpTransport, journal: Journal, shell: Shell, wait_seconds: int = 30):
        self.api, self.journal, self.shell, self.wait_seconds = api, journal, shell, wait_seconds

    def upload_completed(self) -> None:
        for row in self.journal.pending_results():
            payload = {"stdout": row["stdout"], "stderr": row["stderr"], "exitCode": row["exit_code"], "timedOut": bool(row["timed_out"])}
            status, _ = self.api.request("POST", f"/api/client/jobs/{row['job_id']}/result", payload)
            if status == 409:
                # Preserve the result for operator review. A conflict is not an
                # acknowledgement and should not be retried in a tight loop.
                self.journal.mark_conflict(row["job_id"])
                LOG.error("result for job %s conflicts with the server; retained locally for review", row["job_id"])
                continue
            if status != 200:
                raise ApiError(f"unexpected result response HTTP {status}")
            self.journal.mark_acknowledged(row["job_id"])

    def run_once(self) -> bool:
        self.upload_completed()
        status, payload = self.api.request("POST", "/api/client/jobs/poll", {"waitSeconds": self.wait_seconds})
        if status == 204:
            return False
        if status != 200:
            raise ApiError(f"unexpected poll response HTTP {status}")
        job = _valid_job(payload)
        new = self.journal.add_received(job)
        state = self.journal.state(job["jobId"])
        if not new or state != "received":
            # Already started means outcome is unknown; never replay after a crash.
            return True
        self.journal.mark_started(job["jobId"])
        try:
            result = self.shell.execute(job["command"], job["workingDirectory"], job["timeoutSeconds"])
        except Exception as exc:
            # The command may have partially run. Record an unknown completion only
            # is unsafe, so leave state=started and require server-side expiry.
            LOG.error("job execution interrupted (%s)", type(exc).__name__)
            return True
        self.journal.mark_completed(job["jobId"], result)
        self.upload_completed()
        return True

    def run_forever(self, retry_delay: float = 2) -> None:
        while True:
            try:
                self.run_once()
            except ApiError as exc:
                LOG.warning("service request failed: %s", exc)
                time.sleep(retry_delay)

