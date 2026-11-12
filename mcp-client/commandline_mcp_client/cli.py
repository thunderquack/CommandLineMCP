from __future__ import annotations

import argparse
import logging
import os
import sys
import time
from contextlib import contextmanager
from pathlib import Path
from dotenv import dotenv_values

from .client import ClientConfig, HttpApi, Journal, Runner
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
    return ClientConfig(server_url, bearer_key, state_path, setting("SHELL", "powershell").lower())


def main() -> None:
    parser = argparse.ArgumentParser(description="CommandLineMCP polling command runner")
    parser.add_argument("--config", type=Path, default=Path(".env"), help="environment configuration file")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    try:
        config = load_config(args.config)
        with single_instance_lock(config.state_path.with_suffix(config.state_path.suffix + ".lock")):
            journal = Journal(config.state_path)
            try:
                Runner(HttpApi(config.server_url, config.bearer_key), journal, create_shell(config.shell)).run_forever()
            finally:
                journal.close()
    except KeyboardInterrupt:
        return
    except (ValueError, RuntimeError, OSError) as exc:
        logging.error("client could not start: %s", exc)
        sys.exit(2)
