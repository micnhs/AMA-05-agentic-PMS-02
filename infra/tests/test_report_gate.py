import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "Test-CohorsResults.ps1"


class ReportGateTests(unittest.TestCase):
    def run_gate(self, total=1, executed=1, passed=1, skipped=0, junit=None):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell 7 is required; report-gate tests must not be skipped.")
        with tempfile.TemporaryDirectory(prefix="cohors-report-gate-") as directory:
            trx = Path(directory) / "acceptance.trx"
            trx.write_text(
                f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                f'<ResultSummary><Counters total="{total}" executed="{executed}" passed="{passed}" '
                f'notExecuted="{skipped}"/></ResultSummary></TestRun>', encoding="utf-8")
            command = [executable, "-NoProfile", "-File", str(SCRIPT), "-TrxPath", str(trx)]
            if junit is not None:
                report = Path(directory) / "e2e-results.xml"
                report.write_text(junit, encoding="utf-8")
                command.extend(["-JUnitPath", str(report)])
            return subprocess.run(command, capture_output=True, text=True, timeout=120)

    def test_complete_nonzero_execution_is_accepted(self):
        result = self.run_gate(junit='<testsuites><testsuite><testcase name="synthetic"/></testsuite></testsuites>')
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_zero_or_skipped_dotnet_tests_cannot_pass(self):
        for counters in ((0, 0, 0, 0), (2, 1, 1, 1), (2, 2, 1, 0)):
            with self.subTest(counters=counters):
                self.assertNotEqual(self.run_gate(*counters).returncode, 0)

    def test_zero_skipped_or_failed_browser_tests_cannot_pass(self):
        for result in ("", '<testcase><skipped/></testcase>', '<testcase><failure/></testcase>'):
            with self.subTest(result=result):
                self.assertNotEqual(self.run_gate(junit=f"<testsuites><testsuite>{result}</testsuite></testsuites>").returncode, 0)


if __name__ == "__main__":
    unittest.main()
