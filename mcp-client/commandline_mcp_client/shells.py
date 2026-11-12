from __future__ import annotations

import base64
import os
import subprocess
import time
from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True)
class ExecutionResult:
    stdout: str
    stderr: str
    exit_code: int | None
    timed_out: bool = False


class Shell(Protocol):
    def execute(self, command: str, working_directory: str, timeout_seconds: float) -> ExecutionResult: ...


class PowerShellShell:
    """Windows PowerShell adapter. Commands run with the client's user rights."""

    def __init__(self, executable: str = "powershell.exe"):
        self.executable = executable

    def execute(self, command: str, working_directory: str, timeout_seconds: float) -> ExecutionResult:
        # EncodedCommand avoids quoting and preserves arbitrary Unicode commands.
        script = "$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n" + command
        encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
        proc = subprocess.Popen(
            [self.executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            cwd=working_directory,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
        )
        try:
            out, err = proc.communicate(timeout=timeout_seconds)
            return ExecutionResult(out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"), proc.returncode)
        except subprocess.TimeoutExpired:
            if os.name == "nt":
                # Terminate descendants too, then collect any output already emitted.
                subprocess.run(["taskkill.exe", "/PID", str(proc.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                proc.kill()
            out, err = proc.communicate()
            return ExecutionResult(out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"), None, True)


def create_shell(name: str) -> Shell:
    if name == "powershell":
        return PowerShellShell()
    # Extension point: register cmd/Linux shell adapters here when implemented.
    raise ValueError(f"unsupported shell: {name}")
