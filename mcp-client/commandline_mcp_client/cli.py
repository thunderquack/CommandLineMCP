from __future__ import annotations

import argparse
import _thread
import logging
import os
import signal
import sys
import time
from contextlib import contextmanager
from pathlib import Path
from dotenv import dotenv_values

from .client import ClientConfig, HttpApi, Journal, Runner, RestartRequested, safe_server_endpoint
from .capabilities import detect_capabilities
from .shells import create_shell


@contextmanager
def single_instance_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    handle = path.open("a+b")
    try:
        if os.name == "nt":
            import msvcrt
            handle.seek(0)
            if handle.read(1) == b"":
                handle.seek(0)
                handle.write(b"0")
                handle.flush()
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    except (OSError, BlockingIOError) as exc:
        raise RuntimeError("another client process already holds this configuration lock") from exc
    finally:
        try:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass
        handle.close()


def load_config(path: Path) -> ClientConfig:
    values = dotenv_values(path, encoding="utf-8")
    def setting(name: str, default: str = "") -> str:
        return os.environ.get(name, values.get(name, default) or default).strip()

    server_url = setting("SERVER_URL")
    bearer_key = setting("BEARER_KEY")
    if not server_url or not bearer_key:
        raise ValueError("SERVER_URL and BEARER_KEY must be configured")
    state_path = Path(setting("STATE_PATH", "client-state.sqlite3")).expanduser()
    if not state_path.is_absolute():
        state_path = path.parent / state_path
    debug_value = setting("DEBUG", "false").lower()
    if debug_value not in ("true", "false", "1", "0", "yes", "no", "on", "off"):
        raise ValueError("DEBUG must be true or false")
    return ClientConfig(server_url, bearer_key, state_path, setting("SHELL", "auto").lower(), debug=debug_value in ("true", "1", "yes", "on"))



def install_windows_console_handler():
    """Forward native CTRL_C_EVENT / CTRL_BREAK_EVENT to Python's SIGINT handler.

    Windows venv uses a launcher process, and console control events can bypass
    Python's normal signal dispatch. A native handler runs on a Windows thread,
    so it must *schedule* an interrupt on the Python main thread, never raise
    KeyboardInterrupt directly on the console callback thread.
    """
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    handler_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.DWORD)
    kernel32.SetConsoleCtrlHandler.argtypes = [handler_type, wintypes.BOOL]
    kernel32.SetConsoleCtrlHandler.restype = wintypes.BOOL

    @handler_type
    def on_console_event(event):
        if event not in (0, 1):  # CTRL_C_EVENT, CTRL_BREAK_EVENT
            return False
        _thread.interrupt_main(signal.SIGINT)
        return True

    if not kernel32.SetConsoleCtrlHandler(on_console_event, True):
        logging.warning("could not install Windows console control handler: %s",
                        ctypes.get_last_error())
        return None
    return kernel32, on_console_event


def main() -> None:
    parser = argparse.ArgumentParser(description="CommandLineMCP polling command runner")
    parser.add_argument("--config", type=Path, default=Path(".env"), help="environment configuration file")
    args = parser.parse_args()
    def on_ctrl_c(_signum, _frame):
        print("\nCtrl+C received, stopping MCP client...", file=sys.stderr, flush=True)
        raise KeyboardInterrupt

    previous_handler = signal.signal(signal.SIGINT, on_ctrl_c)
    windows_handler = install_windows_console_handler()
    restart = False
    try:
        config = load_config(args.config)
        logging.basicConfig(level=logging.DEBUG if config.debug else logging.INFO,
                            format="%(asctime)s %(levelname)s %(message)s")
        logging.info("starting client: shell=%s server=%s", config.shell, safe_server_endpoint(config.server_url))
        capabilities = detect_capabilities(config.shell)
        logging.info("detected OS=%s version=%s architecture=%s; shells=%s; default=%s",
                     capabilities["operatingSystem"], capabilities["osVersion"],
                     capabilities["architecture"],
                     ", ".join(item["id"] for item in capabilities["shells"]),
                     capabilities["defaultShell"])
        with single_instance_lock(config.state_path.with_suffix(config.state_path.suffix + ".lock")):
            journal = Journal(config.state_path)
            try:
                Runner(HttpApi(config.server_url, config.bearer_key), journal,
                       create_shell(capabilities["defaultShell"]), capabilities=capabilities).run_forever()
            finally:
                journal.close()
    except RestartRequested:
        restart = True
    except KeyboardInterrupt:
        logging.info("MCP client stopped")
        return
    except (ValueError, RuntimeError, OSError) as exc:
        logging.error("client could not start: %s", exc)
        sys.exit(2)
    finally:
        if windows_handler is not None:
            kernel32, handler = windows_handler
            kernel32.SetConsoleCtrlHandler(handler, False)
        signal.signal(signal.SIGINT, previous_handler)
    if restart:
        # The journal is closed and the single-instance lock is released.
        # execv starts a fresh interpreter, so updated .py files are loaded.
        logging.info("restarting with fresh interpreter: %s", sys.executable)
        os.execv(sys.executable, [sys.executable, *sys.argv])
