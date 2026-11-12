from __future__ import annotations

import base64
import os
import signal
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
    """PowerShell 7 adapter. Commands run with the client's user rights."""

    def __init__(self, executable: str = "pwsh"):
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
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) if os.name == "nt" else 0,
            start_new_session=os.name != "nt",
        )
        try:
            out, err = proc.communicate(timeout=timeout_seconds)
            return ExecutionResult(out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"), proc.returncode)
        except (subprocess.TimeoutExpired, KeyboardInterrupt) as exc:
            if os.name == "nt":
                # Terminate descendants too, then collect any output already emitted.
                subprocess.run(["taskkill.exe", "/PID", str(proc.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            out, err = proc.communicate()
            if isinstance(exc, KeyboardInterrupt):
                raise
            return ExecutionResult(out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"), None, True)



class PosixShell:
    """Run a POSIX shell explicitly, without relying on subprocess shell=True."""

    def __init__(self, executable: str):
        self.executable = executable

    def command_arguments(self, command: str) -> list[str]:
        return ["-c", command]

    def execute(self, command: str, working_directory: str, timeout_seconds: float) -> ExecutionResult:
        proc = subprocess.Popen(
            [self.executable, *self.command_arguments(command)],
            cwd=working_directory, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) if os.name == "nt" else 0,
            start_new_session=os.name != "nt",
        )
        try:
            out, err = proc.communicate(timeout=timeout_seconds)
            return ExecutionResult(out.decode("utf-8", errors="replace"),
                                   err.decode("utf-8", errors="replace"), proc.returncode)
        except (subprocess.TimeoutExpired, KeyboardInterrupt) as exc:
            if os.name == "nt":
                subprocess.run(["taskkill.exe", "/PID", str(proc.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            out, err = proc.communicate()
            if isinstance(exc, KeyboardInterrupt):
                raise
            return ExecutionResult(out.decode("utf-8", errors="replace"),
                                   err.decode("utf-8", errors="replace"), None, True)


class CmdShell(PosixShell):
    """Windows cmd.exe executor with AutoRun disabled."""
    def __init__(self):
        super().__init__("cmd.exe")

    def command_arguments(self, command: str) -> list[str]:
        return ["/d", "/s", "/c", command]


def create_shell(name: str) -> Shell:
    if name == "pwsh":
        return PowerShellShell("pwsh")
    if name == "powershell":
        return PowerShellShell("powershell.exe")
    if name in ("bash", "sh"):
        return PosixShell(name)
    if name == "cmd":
        return CmdShell()
    raise ValueError(f"unsupported shell: {name}")
