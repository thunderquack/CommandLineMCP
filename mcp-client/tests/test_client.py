import tempfile
import unittest
import os
import threading
from unittest.mock import patch
from pathlib import Path

from commandline_mcp_client.cli import load_config
from commandline_mcp_client.client import ApiError, Heartbeat, Journal, Runner, RestartRequested, _valid_job, safe_server_endpoint
from commandline_mcp_client.shells import ExecutionResult, PowerShellShell, create_shell


class FakeApi:
    def __init__(self, jobs=(), result_status=200):
        self.jobs = list(jobs)
        self.uploads = []
        self.result_status = result_status

    def request(self, method, path, payload=None):
        if path == "/api/client/jobs/poll":
            return (200, self.jobs.pop(0)) if self.jobs else (204, None)
        if path.endswith("/result"):
            self.uploads.append((path, payload))
            return self.result_status, None
        raise AssertionError(path)


JOB = {"jobId": "job-1", "command": "echo hello", "workingDirectory": ".", "timeoutSeconds": 10}


class FakeShell:
    def __init__(self, result=None):
        self.calls = []
        self.result = result or ExecutionResult("ok", "", 0)

    def execute(self, command, working_directory, timeout_seconds):
        self.calls.append((command, working_directory, timeout_seconds))
        return self.result


class ClientTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.journal = Journal(Path(self.temp.name) / "state.sqlite3")

    def tearDown(self):
        self.journal.close()
        self.temp.cleanup()

    @unittest.skipUnless(os.name == "nt", "native console events are Windows-only")
    def test_windows_console_handler_forwards_ctrl_c_and_ctrl_break(self):
        from commandline_mcp_client.cli import install_windows_console_handler
        with patch("commandline_mcp_client.cli._thread.interrupt_main") as trigger:
            registration = install_windows_console_handler()
            self.assertIsNotNone(registration)
            kernel32, handler = registration
            try:
                self.assertTrue(handler(0))
                self.assertTrue(handler(1))
                self.assertFalse(handler(2))
                self.assertEqual(trigger.call_count, 2)
            finally:
                kernel32.SetConsoleCtrlHandler(handler, False)

    def test_heartbeat_runs_independently_and_stops(self):
        seen = threading.Event()
        class HeartbeatApi:
            def __init__(self):
                self.calls = 0
            def request(self, method, path, payload=None):
                self.calls += 1
                self_method, self_path = method, path
                assert (self_method, self_path) == ("POST", "/api/client/heartbeat")
                seen.set()
                return 200, None
        api = HeartbeatApi()
        heartbeat = Heartbeat(api, interval_seconds=0.05)
        heartbeat.start()
        self.assertTrue(seen.wait(2))
        heartbeat.stop()
        calls = api.calls
        threading.Event().wait(0.1)
        self.assertEqual(calls, api.calls)

    def test_heartbeat_disables_itself_for_older_server(self):
        class LegacyApi:
            def __init__(self):
                self.calls = 0
            def request(self, method, path, payload=None):
                self.calls += 1
                raise ApiError("server returned HTTP 404")
        api = LegacyApi()
        heartbeat = Heartbeat(api, interval_seconds=0.03)
        heartbeat.start()
        self.assertIsNotNone(heartbeat._thread)
        heartbeat._thread.join(timeout=1)
        self.assertFalse(heartbeat._thread.is_alive())
        self.assertEqual(api.calls, 1)
        heartbeat.stop()

    def test_restart_acknowledged_without_shell_execution(self):
        job = {**JOB, "kind": "restart", "command": "__mcp_restart__"}
        api, shell = FakeApi([job]), FakeShell()
        with self.assertRaises(RestartRequested):
            Runner(api, self.journal, shell).run_once()
        self.assertEqual(shell.calls, [])
        self.assertEqual(self.journal.state(job["jobId"]), "acknowledged")
        self.assertEqual(api.uploads[0][1]["exitCode"], 0)

    def test_restart_retries_upload_before_relaunch(self):
        class FlakyRestartApi(FakeApi):
            def __init__(self, job):
                super().__init__([job])
                self.attempts = 0

            def request(self, method, path, payload=None):
                if path.endswith("/result"):
                    self.attempts += 1
                    if self.attempts == 1:
                        raise ApiError("temporarily offline")
                return super().request(method, path, payload)

        job = {**JOB, "kind": "restart", "command": "__mcp_restart__"}
        api, shell = FlakyRestartApi(job), FakeShell()
        runner = Runner(api, self.journal, shell)
        with self.assertRaises(ApiError):
            runner.run_once()
        self.assertEqual(self.journal.state(job["jobId"]), "completed")
        with self.assertRaises(RestartRequested):
            runner.run_once()
        self.assertEqual(api.attempts, 2)
        self.assertEqual(shell.calls, [])

    def test_old_journal_migrates_kind_column(self):
        import sqlite3
        path = Path(self.temp.name) / "legacy.sqlite3"
        db = sqlite3.connect(path)
        try:
            db.execute("""CREATE TABLE jobs (
                job_id TEXT PRIMARY KEY, command TEXT NOT NULL, working_directory TEXT NOT NULL,
                timeout_seconds REAL NOT NULL, state TEXT NOT NULL, stdout TEXT, stderr TEXT,
                exit_code INTEGER, timed_out INTEGER, received_at REAL NOT NULL, updated_at REAL NOT NULL
            )""")
            db.commit()
        finally:
            db.close()
        journal = Journal(path)
        try:
            self.assertTrue(journal.add_received(JOB))
            row = journal.db.execute("SELECT kind FROM jobs WHERE job_id=?", (JOB["jobId"],)).fetchone()
            self.assertEqual(row["kind"], "command")
        finally:
            journal.close()

    def test_explicit_shell_uses_selected_adapter_not_default(self):
        api, default = FakeApi([{**JOB, "shell": "cmd", "command": "echo CMD_JOB"}]), FakeShell()
        caps = {"shells": [{"id": "pwsh"}, {"id": "cmd"}]}
        with patch("commandline_mcp_client.client.create_shell") as create:
            cmd = FakeShell(ExecutionResult("CMD_JOB\n", "", 0))
            create.return_value = cmd
            runner = Runner(api, self.journal, default, capabilities=caps)
            runner._capabilities_published = True
            runner.run_once()
        create.assert_called_once_with("cmd")
        self.assertEqual(default.calls, [])
        self.assertEqual(cmd.calls, [("echo CMD_JOB", ".", 10.0)])
        row = self.journal.db.execute("SELECT shell FROM jobs WHERE job_id=?", (JOB["jobId"],)).fetchone()
        self.assertEqual(row["shell"], "cmd")

    def test_unsupported_shell_fails_without_execution(self):
        api, default = FakeApi([{**JOB, "shell": "bash"}]), FakeShell()
        caps = {"shells": [{"id": "pwsh"}]}
        runner = Runner(api, self.journal, default, capabilities=caps)
        runner._capabilities_published = True
        runner.run_once()
        self.assertEqual(default.calls, [])
        self.assertEqual(api.uploads[0][1]["exitCode"], 1)
        self.assertIn("Unsupported shell", api.uploads[0][1]["stderr"])

    def test_invalid_shell_payload_rejected(self):
        with self.assertRaises(ApiError):
            _valid_job({**JOB, "shell": ["pwsh"]})

    def test_persists_result_before_upload_and_uploads_result(self):
        api, shell = FakeApi([JOB]), FakeShell(ExecutionResult("héllo", "", 0))
        Runner(api, self.journal, shell).run_once()
        self.assertEqual(shell.calls, [("echo hello", ".", 10.0)])
        self.assertEqual(api.uploads[0][1], {"stdout": "héllo", "stderr": "", "exitCode": 0, "timedOut": False})
        self.assertEqual(self.journal.pending_results(), [])

    def test_logs_job_lifecycle_without_command_or_output(self):
        api = FakeApi([{**JOB, "command": "secret-command"}])
        runner = Runner(api, self.journal, FakeShell(ExecutionResult("secret-output", "secret-error", 3)))
        with self.assertLogs("commandline_mcp_client", level="INFO") as captured:
            runner.run_once()
        logs = "\n".join(captured.output)
        self.assertIn("connected to server; waiting for jobs", logs)
        self.assertIn("job job-1 received", logs)
        self.assertIn("execution started for job job-1", logs)
        self.assertIn("exit code 3", logs)
        self.assertIn("result acknowledged for job job-1", logs)
        self.assertNotIn("secret-command", logs)
        self.assertNotIn("secret-output", logs)
        self.assertNotIn("secret-error", logs)

    def test_short_initial_poll_then_normal_long_poll(self):
        class RecordingApi(FakeApi):
            def __init__(self):
                super().__init__()
                self.poll_waits = []

            def request(self, method, path, payload=None):
                if path == "/api/client/jobs/poll":
                    self.poll_waits.append(payload["waitSeconds"])
                return super().request(method, path, payload)

        api = RecordingApi()
        runner = Runner(api, self.journal, FakeShell(), wait_seconds=30)
        with self.assertLogs("commandline_mcp_client", level="INFO") as captured:
            runner.run_once()
            runner.run_once()
        self.assertEqual(api.poll_waits, [2, 30])
        self.assertIn("connecting to server; checking availability...", "\n".join(captured.output))
        self.assertEqual(sum("connected to server; waiting for jobs" in line for line in captured.output), 1)

    def test_debug_logs_command_and_result(self):
        api = FakeApi([{**JOB, "command": "Write-Output debug-test"}])
        with self.assertLogs("commandline_mcp_client", level="DEBUG") as captured:
            Runner(api, self.journal, FakeShell(ExecutionResult("hello", "warning", 3))).run_once()
        logs = "\n".join(captured.output)
        self.assertIn("Write-Output debug-test", logs)
        self.assertIn("hello", logs)
        self.assertIn("warning", logs)
        self.assertIn("exitCode=3", logs)

    def test_poll_propagates_background_request_error(self):
        class ErrorApi(FakeApi):
            def request(self, method, path, payload=None):
                raise ApiError("poll failed")

        with self.assertRaisesRegex(ApiError, "poll failed"):
            Runner(ErrorApi(), self.journal, FakeShell()).run_once()

    def test_capabilities_publish_before_poll(self):
        class CapabilityApi(FakeApi):
            def __init__(self):
                super().__init__()
                self.requests = []

            def request(self, method, path, payload=None):
                self.requests.append((method, path, payload))
                if path == "/api/client/capabilities":
                    return 200, None
                return super().request(method, path, payload)

        api = CapabilityApi()
        data = {"operatingSystem": "Linux", "osVersion": "1", "architecture": "x86_64",
                "defaultShell": "bash", "shells": [{"id": "bash", "version": "5"}]}
        runner = Runner(api, self.journal, FakeShell(), capabilities=data)
        runner.run_once()
        runner.run_once()
        self.assertEqual(api.requests[0], ("PUT", "/api/client/capabilities", data))
        self.assertEqual([x[1] for x in api.requests].count("/api/client/capabilities"), 1)

    def test_old_server_without_capabilities_still_polls(self):
        class LegacyApi(FakeApi):
            def request(self, method, path, payload=None):
                if path == "/api/client/capabilities":
                    raise ApiError("server returned HTTP 404")
                return super().request(method, path, payload)

        with self.assertLogs("commandline_mcp_client", level="WARNING") as captured:
            self.assertFalse(Runner(LegacyApi(), self.journal, FakeShell(),
                                    capabilities={"shells": []}).run_once())
        self.assertIn("does not support capabilities", "\n".join(captured.output))

    def test_debug_config_env_override(self):
        path = Path(self.temp.name) / ".env"
        path.write_text("SERVER_URL=https://example.org\nBEARER_KEY=secret\nDEBUG=true\n", encoding="utf-8")
        with patch.dict(os.environ, {"DEBUG": "false"}):
            self.assertFalse(load_config(path).debug)
        with patch.dict(os.environ, {"DEBUG": "true"}):
            self.assertTrue(load_config(path).debug)

    def test_connection_status_logs_only_transitions(self):
        class FlakyApi(FakeApi):
            def __init__(self):
                super().__init__()
                self.calls = 0

            def request(self, method, path, payload=None):
                self.calls += 1
                if self.calls in (3, 4, 6, 7):
                    raise ApiError("temporary failure")
                return super().request(method, path, payload)

        api = FlakyApi()
        runner = Runner(api, self.journal, FakeShell())
        sleep_calls = 0

        def stop_after_repeated_outage(_delay):
            nonlocal sleep_calls
            sleep_calls += 1
            if sleep_calls == 4:
                raise RuntimeError("stop test loop")

        with self.assertLogs("commandline_mcp_client", level="INFO") as captured:
            with patch("commandline_mcp_client.client.time.sleep", side_effect=stop_after_repeated_outage):
                with self.assertRaisesRegex(RuntimeError, "stop test loop"):
                    runner.run_forever(retry_delay=0)
        warnings = [entry for entry in captured.output if "server offline" in entry]
        self.assertEqual(len(warnings), 2)
        self.assertEqual(sum("connected to server" in entry for entry in captured.output), 2)

    def test_safe_endpoint_strips_url_credentials_and_query(self):
        endpoint = safe_server_endpoint("https://private:secret@cmd.example.com/api?token=hidden#fragment")
        self.assertEqual(endpoint, "https://cmd.example.com/api")

    def test_job_id_is_never_executed_twice(self):
        api, shell = FakeApi([JOB, JOB]), FakeShell()
        runner = Runner(api, self.journal, shell)
        runner.run_once()
        runner.run_once()
        self.assertEqual(len(shell.calls), 1)

    def test_started_interrupted_job_is_unknown_and_never_replayed(self):
        self.journal.add_received(JOB)
        self.journal.mark_started("job-1")
        shell = FakeShell()
        Runner(FakeApi([JOB]), self.journal, shell).run_once()
        self.assertEqual(shell.calls, [])
        self.assertEqual(self.journal.state("job-1"), "started")

    def test_missing_working_directory_defaults_to_current_directory_and_recovers(self):
        job = {**JOB, "workingDirectory": None}
        normalized = _valid_job(job)
        self.assertEqual(normalized["workingDirectory"], str(Path.cwd()))
        self.journal.add_received(normalized)
        self.journal.mark_started("job-1")
        shell = FakeShell()
        Runner(FakeApi([job]), self.journal, shell).run_once()
        self.assertEqual(shell.calls, [])
        self.assertEqual(self.journal.state("job-1"), "started")

    def test_env_file_and_environment_precedence_and_relative_state_path(self):
        config_dir = Path(self.temp.name) / "config"
        config_dir.mkdir()
        config_path = config_dir / ".env"
        config_path.write_text(
            "SERVER_URL=https://cmd.example.com\nBEARER_KEY=file-key\n"
            "STATE_PATH=state/client.sqlite3\nSHELL=powershell\n",
            encoding="utf-8",
        )
        with patch.dict(os.environ, {"BEARER_KEY": "env-key", "SHELL": "powershell"}):
            config = load_config(config_path)
        self.assertEqual(config.server_url, "https://cmd.example.com")
        self.assertEqual(config.bearer_key, "env-key")
        self.assertEqual(config.state_path, config_dir / "state" / "client.sqlite3")

    def test_timeout_result_is_durable_and_reports_timeout(self):
        api = FakeApi([JOB])
        Runner(api, self.journal, FakeShell(ExecutionResult("partial", "", None, True))).run_once()
        self.assertEqual(api.uploads[0][1]["timedOut"], True)
        self.assertIsNone(api.uploads[0][1]["exitCode"])

    def test_conflicting_result_is_retained_and_not_marked_acknowledged(self):
        api, shell = FakeApi([JOB], result_status=409), FakeShell()
        with self.assertLogs("commandline_mcp_client", level="ERROR") as captured:
            Runner(api, self.journal, shell).run_once()
        self.assertEqual(self.journal.state("job-1"), "conflict")
        self.assertEqual(self.journal.pending_results(), [])
        self.assertEqual(len(api.uploads), 1)
        self.assertIn("retained locally for review", captured.output[0])

    def test_completed_result_is_retried_without_reexecuting_after_disconnect(self):
        class FailingFirstUpload(FakeApi):
            def __init__(self):
                super().__init__([JOB])
                self.fail_once = True

            def request(self, method, path, payload=None):
                if path.endswith("/result") and self.fail_once:
                    self.fail_once = False
                    raise OSError("simulated disconnect")
                return super().request(method, path, payload)

        api, shell = FailingFirstUpload(), FakeShell()
        runner = Runner(api, self.journal, shell)
        with self.assertRaises(OSError):
            runner.run_once()
        self.assertEqual(self.journal.state("job-1"), "completed")
        runner.run_once()
        self.assertEqual(len(shell.calls), 1)
        self.assertEqual(self.journal.state("job-1"), "acknowledged")

    @unittest.skipUnless(__import__("os").name == "nt", "PowerShell integration requires Windows")
    def test_harmless_powershell_unicode_output(self):
        result = PowerShellShell().execute("Write-Output 'Привет мир'; exit 0", str(Path.cwd()), 15)
        self.assertEqual(result.exit_code, 0)
        self.assertIn("Привет мир", result.stdout)

    @unittest.skipUnless(__import__("os").name == "nt", "cmd integration requires Windows")
    def test_cmd_shell_executes_command(self):
        result = create_shell("cmd").execute("echo CMD_MCP_TEST", str(Path.cwd()), 10)
        self.assertEqual(result.exit_code, 0)
        self.assertIn("CMD_MCP_TEST", result.stdout)

    @unittest.skipUnless(__import__("os").name == "nt", "PowerShell integration requires Windows")
    def test_default_shell_is_powershell_7(self):
        result = PowerShellShell().execute("$PSVersionTable.PSVersion.Major", str(Path.cwd()), 15)
        self.assertEqual(result.exit_code, 0)
        self.assertEqual(result.stdout.strip(), "7")

    @unittest.skipUnless(__import__("os").name == "nt", "process-tree integration requires Windows")
    def test_timeout_kills_powershell_process_tree_and_keeps_partial_output(self):
        import os
        import time

        child_pid_file = Path(self.temp.name) / "child.pid"
        command = (
            "$child = Start-Process pwsh.exe -PassThru -WindowStyle Hidden "
            "-ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30'); "
            f"Set-Content -LiteralPath '{child_pid_file}' -Value $child.Id; "
            "Write-Output 'before timeout'; Start-Sleep -Seconds 30"
        )
        result = PowerShellShell().execute(command, self.temp.name, 2)
        self.assertTrue(result.timed_out)
        self.assertIn("before timeout", result.stdout)
        self.assertTrue(child_pid_file.exists())
        child_pid = int(child_pid_file.read_text().strip())
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                os.kill(child_pid, 0)
            except OSError:
                break
            time.sleep(0.1)
        else:
            self.fail("timed-out PowerShell child process remained alive")


if __name__ == "__main__":
    unittest.main()
