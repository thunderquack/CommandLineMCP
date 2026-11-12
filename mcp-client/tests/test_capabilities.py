import unittest
from unittest.mock import patch
from commandline_mcp_client.capabilities import detect_capabilities


class CapabilityTests(unittest.TestCase):
    def test_discovery_returns_platform_and_default(self):
        with patch("commandline_mcp_client.capabilities.shutil.which",
                   side_effect=lambda executable: executable if executable in ("pwsh.exe", "bash") else None), \
             patch("commandline_mcp_client.capabilities.platform.system", return_value="Windows"), \
             patch("commandline_mcp_client.capabilities.platform.version", return_value="10.0"), \
             patch("commandline_mcp_client.capabilities.platform.machine", return_value="AMD64"), \
             patch("commandline_mcp_client.capabilities.subprocess.run") as run:
            run.return_value.returncode = 0
            run.return_value.stdout = "7.7.0\n"
            result = detect_capabilities()
        self.assertEqual(result["defaultShell"], "pwsh")
        self.assertEqual(result["operatingSystem"], "Windows")
        self.assertEqual(result["architecture"], "AMD64")
        self.assertEqual([s["id"] for s in result["shells"]], ["pwsh", "bash"])

    def test_auto_selects_bash_on_linux(self):
        with patch("commandline_mcp_client.capabilities.shutil.which",
                   side_effect=lambda executable: executable if executable in ("bash", "sh") else None), \
             patch("commandline_mcp_client.capabilities.platform.system", return_value="Linux"), \
             patch("commandline_mcp_client.capabilities.subprocess.run") as run:
            run.return_value.returncode = 1
            result = detect_capabilities("auto")
        self.assertEqual(result["defaultShell"], "bash")
        self.assertEqual([x["id"] for x in result["shells"]], ["bash", "sh"])

    def test_windows_wsl_launcher_is_not_native_bash(self):
        with patch("commandline_mcp_client.capabilities.shutil.which",
                   side_effect=lambda executable: "C:/Windows/System32/bash.exe" if executable == "bash"
                   else "C:/Program Files/PowerShell/7/pwsh.exe" if executable == "pwsh.exe"
                   else None), \
             patch("commandline_mcp_client.capabilities.platform.system", return_value="Windows"), \
             patch("commandline_mcp_client.capabilities.subprocess.run") as run:
            run.return_value.returncode = 0
            run.return_value.stdout = "7.7.0\n"
            result = detect_capabilities("auto")
        self.assertEqual([x["id"] for x in result["shells"]], ["pwsh"])

    def test_missing_default_is_rejected(self):
        with patch("commandline_mcp_client.capabilities.shutil.which", return_value=None):
            with self.assertRaisesRegex(ValueError, "not available"):
                detect_capabilities()
