"""Cross-platform discovery of the operating system and supported shells."""

from __future__ import annotations

import platform
import shutil
import subprocess


SHELL_CANDIDATES = (
    ("pwsh", "pwsh.exe" if platform.system() == "Windows" else "pwsh"),
    ("powershell", "powershell.exe"),
    ("bash", "bash"),
    ("sh", "sh"),
)


def detect_capabilities(configured_shell: str = "powershell") -> dict:
    """Report only shells that can be launched in the client's environment.

    The legacy configuration value 'powershell' launches PowerShell 7 (pwsh).
    Each probe is bounded so an installed but unresponsive shell won't stall
    startup. Executable paths are never sent to the server.
    """
    shells = []
    for shell_id, executable in SHELL_CANDIDATES:
        path = shutil.which(executable)
        if path is None:
            continue
        # Windows' system32/WindowsApps bash.exe is a WSL launcher, not a
        # native Bash shell. WSL distributions need separate discovery.
        if platform.system() == "Windows" and shell_id == "bash":
            normalized = path.replace("/", "\\").lower()
            if normalized.endswith("\\system32\\bash.exe") or "\\windowsapps\\" in normalized:
                continue
        # Versions are informational; a version probe failure doesn't make a
        # shell unavailable.
        version = None
        if shell_id in ("pwsh", "powershell"):
            args = [path, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "$PSVersionTable.PSVersion.ToString()"]
        else:
            args = [path, "--version"]
        try:
            completed = subprocess.run(args, capture_output=True, text=True,
                                       timeout=3, stdin=subprocess.DEVNULL)
            if completed.returncode == 0:
                first_line = completed.stdout.strip().splitlines()
                if first_line:
                    version = first_line[0][:128]
        except (OSError, subprocess.TimeoutExpired, UnicodeError):
            pass
        shells.append({"id": shell_id, "version": version})

    available = {item["id"] for item in shells}
    if configured_shell == "auto":
        preference = ("pwsh", "powershell", "bash", "sh") if platform.system() == "Windows" else ("bash", "sh", "pwsh")
        default_shell = next((candidate for candidate in preference if candidate in available), "")
    else:
        default_shell = "pwsh" if configured_shell == "powershell" else configured_shell
    if default_shell not in available:
        raise ValueError(f"configured shell {configured_shell!r} is not available")

    return {
        "operatingSystem": platform.system(),
        "osVersion": platform.version()[:256],
        "architecture": platform.machine()[:64],
        "defaultShell": default_shell,
        "shells": shells,
    }
