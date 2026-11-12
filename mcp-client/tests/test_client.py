import tempfile
import unittest
from pathlib import Path

from commandline_mcp_client.client import Journal, Runner
from commandline_mcp_client.shells import ExecutionResult, PowerShellShell


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

    def test_persists_result_before_upload_and_uploads_result(self):
        api, shell = FakeApi([JOB]), FakeShell(ExecutionResult("héllo", "", 0))
        Runner(api, self.journal, shell).run_once()
        self.assertEqual(shell.calls, [("echo hello", ".", 10.0)])
        self.assertEqual(api.uploads[0][1], {"stdout": "héllo", "stderr": "", "exitCode": 0, "timedOut": False})
        self.assertEqual(self.journal.pending_results(), [])

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

    @unittest.skipUnless(__import__("os").name == "nt", "process-tree integration requires Windows")
    def test_timeout_kills_powershell_process_tree_and_keeps_partial_output(self):
        import os
        import time

        child_pid_file = Path(self.temp.name) / "child.pid"
        command = (
            "$child = Start-Process powershell.exe -PassThru -WindowStyle Hidden "
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
