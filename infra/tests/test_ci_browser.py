import base64
import json
import re
import shutil
import subprocess
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
PROBE = r"""
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json -AsHashtable
function ParseSource([string]$path) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Actual browser integration source does not parse.' }
    return $ast
}
$ast = ParseSource $inputData.Script
foreach ($name in @('Get-CiBrowserProfiles', 'Get-CiBrowserEvidencePaths', 'Assert-CiReportCredentials',
        'Assert-CiBrowserCleanup', 'Add-CiBrowserReport')) {
    $nodes = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($nodes.Count -ne 1) { throw 'Expected one actual pure CI function.' }
    . ([scriptblock]::Create($nodes[0].Extent.Text))
}
switch ($inputData.Operation) {
    'profiles' {
        $runner = ParseSource $inputData.Runner
        $assignments = @($runner.FindAll({
            param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and
            (($node.Left.Extent.Text -ceq '$project' -and $node.Right.Extent.Text.StartsWith('switch ($Profile)')) -or
                $node.Left.Extent.Text -ceq '$expected')
        }, $true))
        if ($assignments.Count -ne 2) { throw 'The real runner profile selection is ambiguous.' }
        $selection = [scriptblock]::Create(($assignments.Extent.Text -join "`n"))
        $profiles = Get-CiBrowserProfiles
        $actual = [ordered]@{}
        foreach ($Profile in $profiles.Keys) {
            . $selection
            $actual[$Profile] = @{ project = $project; tests = $expected }
        }
        @{ planned = $profiles; runner = $actual
            artifactPaths = @((Get-CiBrowserEvidencePaths 'synthetic-owned-root').Keys) } | ConvertTo-Json -Depth 6 -Compress
    }
    'merge' {
        $results = foreach ($scenario in $inputData.Scenarios) {
            [xml]$result = '<testsuites name="Synthetic aggregate" tests="0" failures="0" errors="0" skipped="0" time="0" />'
            try {
                foreach ($entry in $scenario) {
                    Add-CiBrowserReport $result ([xml]$entry.Xml) $entry.Project $entry.ExpectedTests @{
                        'cohors.profile' = $entry.Project; 'cohors.cleanupProven' = 'true'
                    }
                }
                @{ accepted = $true; xml = $result.OuterXml; error = '' }
            } catch { @{ accepted = $false; xml = $result.OuterXml; error = $_.Exception.Message } }
        }
        ConvertTo-Json -InputObject @($results) -Depth 5 -Compress
    }
    'cleanup' {
        $results = foreach ($terminal in $inputData.Terminals) {
            try { Assert-CiBrowserCleanup $terminal; @{ accepted = $true; error = '' } }
            catch { @{ accepted = $false; error = $_.Exception.Message } }
        }
        ConvertTo-Json -InputObject @($results) -Compress
    }
    'host' {
        $runner = ParseSource $inputData.Runner
        $guards = @($runner.FindAll({
            param($node)
            $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.StartsWith('if ($HostedCi)') -and $node.Extent.Text.Contains('GITHUB_WORKSPACE')
        }, $true))
        if ($guards.Count -ne 1) { throw 'Expected the one actual Hosted opt-in gate.' }
        $guard = [scriptblock]::Create($guards[0].Extent.Text.Replace('$IsLinux', '$linux').
            Replace('$env:GITHUB_ACTIONS', '$actions').Replace('$env:GITHUB_WORKSPACE', '$workspace'))
        $results = foreach ($case in $inputData.Cases) {
            $HostedCi = $case.Hosted; $linux = $case.Linux; $actions = $case.Actions
            $root = [IO.Path]::GetDirectoryName($inputData.Runner)
            $workspace = if ($case.SameWorkspace) { $root } else { Join-Path $root 'not-the-checkout' }
            $CredentialInventory = $null
            if ($case.Inventory -ge 0) {
                $CredentialInventory = [Collections.Generic.List[string]]::new()
                if ($case.Inventory -gt 0) { $CredentialInventory.Add('Synthetic existing inventory') }
            }
            try { & $guard; $true } catch { $false }
        }
        ConvertTo-Json -InputObject @($results) -Compress
    }
    'credentials' {
        $runner = ParseSource $inputData.Runner
        $blocks = @($runner.FindAll({
            param($node)
            $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text.StartsWith('if ($HostedCi)') -and $node.Extent.Text.Contains('$CredentialInventory.Add')
        }, $true))
        if ($blocks.Count -ne 1) { throw 'Expected the actual scoped credential registration block.' }
        $register = [scriptblock]::Create($runner.ParamBlock.Extent.Text + "`n" + $blocks[0].Extent.Text)
        $inventory = [Collections.Generic.List[string]]::new()
        $inventory.Add('Synthetic existing inventory')
        $secret = 'Synthetic!ScopedAccount42'
        [byte[]]$salt = @(1, 2, 3); [byte[]]$hash = @(4, 5, 6)
        $masked = @(& $register -ApplicationDirectory 'not-opened' -ArtifactDirectory 'not-created' `
            -HostedCi -CredentialInventory $inventory)
        @{ inventory = $inventory.ToArray(); masked = $masked } | ConvertTo-Json -Compress
    }
    'reports' {
        $inventory = [Collections.Generic.List[string]]::new()
        foreach ($value in $inputData.Prohibited) { $inventory.Add($value) }
        $results = foreach ($case in $inputData.Cases) {
            $bytes = [Convert]::FromBase64String($case)
            $before = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            $accepted = $true; $errorText = ''
            try { Assert-CiReportCredentials $bytes $inventory }
            catch { $accepted = $false; $errorText = $_.Exception.Message }
            @{ accepted = $accepted; error = $errorText
                unchanged = $before -ceq [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
        }
        ConvertTo-Json -InputObject @($results) -Compress
    }
    default { throw 'Unknown pure-source probe.' }
}
"""


def report(project, outcomes):
    counters = dict(tests=str(len(outcomes)), failures=str(outcomes.count("failure")),
                    errors=str(outcomes.count("error")), skipped=str(outcomes.count("skipped")), time="1.25")
    root = ET.Element("testsuites", {"name": "", **counters})
    suite = ET.SubElement(root, "testsuite", {"name": "pilot.spec.ts", "hostname": project, **counters})
    for index, outcome in enumerate(outcomes):
        case = ET.SubElement(suite, "testcase", name=f"Synthetic case {index}", classname="pilot.spec.ts", time="0.1")
        if outcome != "passed":
            ET.SubElement(case, outcome, message=f"Original synthetic {outcome}").text = "Retain the original detail."
    return {"Project": project, "ExpectedTests": len(outcomes), "Xml": ET.tostring(root, encoding="unicode")}


class CiBrowserTests(unittest.TestCase):
    def probe(self, operation, **values):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell is required; source-contract checks must not be skipped.")
        result = subprocess.run(
            [executable, "-NoProfile", "-NonInteractive", "-Command", PROBE],
            input=json.dumps({"Script": str(ROOT / "infra/scripts/Invoke-CohorsCi.ps1"),
                              "Runner": str(ROOT / "scripts/Test-Ui.ps1"), "Operation": operation, **values}),
            capture_output=True, text=True, encoding="utf-8", timeout=120,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, "")
        return json.loads(result.stdout)

    def test_all_configured_projects_and_cases_use_the_existing_runner_selection(self):
        profiles = self.probe("profiles")
        self.assertEqual(profiles["planned"], profiles["runner"])
        config = (ROOT / "src/Cohors.Web/playwright.config.ts").read_text(encoding="utf-8")
        source = (ROOT / "src/Cohors.Web/e2e/pilot.spec.ts").read_text(encoding="utf-8")
        projects = re.findall(r"\{ name: '([^']+)', (grepInvert|grep): /([^/]+)/ \}", config)
        declarations = re.findall(r"^test\('([^']+)'\s*,\s*(?:\{\s*tag:\s*'([^']+)'\s*\}\s*,)?", source, re.MULTILINE)
        titles = [f"{title} {tag}".strip() for title, tag in declarations]
        titles += re.findall(r"\{ title: '(S0[12]:[^']+)'", source)
        expected = {}
        for project, kind, pattern in projects:
            expected[project] = sum(bool(re.search(pattern, title)) == (kind == "grep") for title in titles)
        self.assertEqual({value["project"]: value["tests"] for value in profiles["planned"].values()}, expected)
        self.assertEqual(len(expected), 11)
        self.assertEqual(sum(expected.values()), 33)
        self.assertNotIn("PrimaryBaseline", profiles["planned"])
        self.assertNotRegex(source, r"\btest\.(?:skip|fixme|only)\s*\(")

    def test_merging_retains_failures_errors_skips_project_identity_and_case_details(self):
        first = report("preview", ["passed", "failure", "error", "skipped"])
        second = report("audit-export", ["passed"])
        result = self.probe("merge", Scenarios=[[first, second]])[0]
        self.assertTrue(result["accepted"], result["error"])
        merged = ET.fromstring(result["xml"])
        self.assertEqual({key: merged.get(key) for key in ("tests", "failures", "errors", "skipped", "time")},
                         {"tests": "5", "failures": "1", "errors": "1", "skipped": "1", "time": "2.5"})
        for suite, original in zip(merged.findall("testsuite"), (first, second), strict=True):
            self.assertEqual(suite.get("hostname"), original["Project"])
            self.assertEqual([ET.tostring(case) for case in suite.findall("testcase")],
                             [ET.tostring(case) for case in ET.fromstring(original["Xml"]).findall("./testsuite/testcase")])
            self.assertEqual(suite.find("./properties/property[@name='cohors.profile']").get("value"), original["Project"])

    def test_rejects_missing_cases_forged_counters_wrong_project_and_duplicate_reports(self):
        original = report("preview", ["passed", "failure"])
        missing = {**original, "ExpectedTests": 3}
        counters = {**original, "Xml": original["Xml"].replace('failures="1"', 'failures="0"')}
        wrong = {**original, "Project": "audit-export"}
        duplicate = {**original, "Xml": original["Xml"].replace("Synthetic case 1", "Synthetic case 0")}
        scenarios = [[missing], [counters], [wrong], [duplicate], [original, original]]
        for result in self.probe("merge", Scenarios=scenarios):
            self.assertFalse(result["accepted"])
            self.assertTrue(result["error"])

    def test_cleanup_allows_failed_browser_only_after_complete_owned_resource_closure(self):
        flags = ("ownApiStopped", "ownDatabaseRemoved", "settingsRemoved", "accessFileRemoved",
                 "objectDirectoryRemoved", "runtimeParentRemoved", "runtimeCreatedByRunner")
        terminal = {**dict.fromkeys(flags, True), "cleanupErrorCount": 0, "intentSha256": "a" * 64,
                    "resourceJournalSha256": "b" * 64, "browserPassed": False, "executionFailed": True}
        invalid = [{**terminal, name: False} for name in flags]
        invalid += [{**terminal, "ownApiStopped": "true"}, {**terminal, "cleanupErrorCount": 1},
                    {**terminal, "cleanupErrorCount": "0"}, {**terminal, "intentSha256": ""},
                    {**terminal, "resourceJournalSha256": None}]
        results = self.probe("cleanup", Terminals=[terminal, *invalid])
        self.assertTrue(results[0]["accepted"], results[0]["error"])
        self.assertTrue(all(not result["accepted"] for result in results[1:]))

    def test_hosted_opt_in_requires_linux_actions_matching_checkout_and_shared_inventory(self):
        allowed = {"Hosted": True, "Linux": True, "Actions": "true", "SameWorkspace": True, "Inventory": 1}
        cases = [allowed, {**allowed, "Hosted": False}, {**allowed, "Linux": False},
                 {**allowed, "Actions": ""}, {**allowed, "SameWorkspace": False},
                 {**allowed, "Inventory": -1}, {**allowed, "Inventory": 0},
                 {**allowed, "Hosted": False, "Actions": "", "Inventory": -1}]
        self.assertEqual(self.probe("host", Cases=cases), [True, False, False, False, False, False, False, True])

    def test_scoped_secrets_extend_the_same_inventory_before_private_runtime_start(self):
        result = self.probe("credentials")
        values = ["Synthetic!ScopedAccount42", "AQID", "BAUG"]
        self.assertEqual(result["inventory"], ["Synthetic existing inventory", *values])
        self.assertEqual(result["masked"], [f"::add-mask::{value}" for value in values])
        source = (ROOT / "scripts/Test-Ui.ps1").read_text(encoding="utf-8")
        self.assertLess(source.index("$CredentialInventory.Add($value)"), source.index("[IO.File]::WriteAllText($settingsPath"))
        self.assertLess(source.index("[IO.File]::WriteAllText($settingsPath"), source.index("[Diagnostics.Process]::Start($start)"))

    def test_all_original_junit_and_terminal_paths_are_explicitly_allowlisted_with_no_private_files(self):
        profiles = self.probe("profiles")
        originals = {
            f"browser-originals/{profile['project']}/{name}"
            for profile in profiles["planned"].values()
            for name in ("e2e-results.xml", "owned-resources-terminal.json")
        }
        self.assertEqual(len(originals), 22)
        self.assertCountEqual(profiles["artifactPaths"], originals)
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-ci.yml").read_text(encoding="utf-8"))
        upload = next(step for step in workflow["jobs"]["acceptance"]["steps"]
                      if step.get("uses", "").startswith("actions/upload-artifact@"))
        expected = originals | {"acceptance.trx", "bootstrap.trx", "e2e-results.xml",
                                "cohors-cockpit.png", "cohors-workbench.png"}
        self.assertCountEqual(upload["with"]["path"].splitlines(),
                              ["${{ runner.temp }}/cohors-test-evidence/" + path for path in expected])

    def test_original_failure_bytes_are_never_sanitized_and_new_scoped_secrets_block_release(self):
        prohibited = ["SyntheticScopedOnly!42", "SyntheticSaltOnly42", "SyntheticHashOnly42"]
        clean = [b'<testsuites><testcase><failure>Original failure\r\nunchanged</failure></testcase></testsuites>',
                 b'{"browserPassed":false,"cleanupErrorCount":1}\n']
        secret_reports = [f'<failure>{value}</failure>'.encode() for value in prohibited]
        secret_reports += [f'{{"synthetic":"{value}"}}'.encode() for value in prohibited]
        invalid = [b'<failure>Authorization: Bearer synthetic-only</failure>', b'\xff']
        cases = [base64.b64encode(value).decode() for value in clean + secret_reports + invalid]
        results = self.probe("reports", Prohibited=prohibited, Cases=cases)
        self.assertEqual([result["accepted"] for result in results], [True, True] + [False] * 8)
        self.assertTrue(all(result["unchanged"] for result in results))
        for result in results[2:]:
            self.assertIn("artifact publication is blocked.", result["error"])
            for value in prohibited:
                self.assertNotIn(value, result["error"])

    def test_ci_serially_uses_owned_profiles_and_never_ignores_failed_cleanup_or_partial_coverage(self):
        source = (ROOT / "infra/scripts/Invoke-CohorsCi.ps1").read_text(encoding="utf-8")
        self.assertNotIn("scripts/Start-Local.ps1", source)
        self.assertNotIn("run test:e2e", source)
        self.assertNotRegex(source, r"\$env:COHORS_(?:URL|TEST_ACCESS_FILE|UI_ARTIFACT_DIR)\s*=")
        self.assertEqual(source.count("(Join-Path $root 'scripts/Test-Ui.ps1')"), 1)
        self.assertIn("-Profile $profile -HostedCi -CredentialInventory $prohibitedValues", source)
        loop = source[source.index("foreach ($profile in $profiles.Keys)"):source.index("if ($reportedProfiles -ne")]
        self.assertLess(loop.index("$browserCleanupProven = $false"), loop.index("'scripts/Test-Ui.ps1'"))
        self.assertLess(loop.index("Assert-CiBrowserCleanup $terminal"), loop.index("$browserCleanupProven = $true"))
        self.assertIn("if (-not $browserCleanupProven) { break }", loop)
        self.assertIn("if ($initialized -and $browserCleanupProven)", source)
        self.assertIn("$reportedProfiles -ne $profiles.Count", source)
        self.assertIn("$junit.DocumentElement.GetAttribute('tests') -ne 33", source)
        self.assertIn("foreach ($value in $prohibitedValues)", source)
        self.assertIn("Get-CiBrowserEvidencePaths $browserRoot", source)
        self.assertLess(source.index("Assert-CiReportCredentials $bytes $prohibitedValues"),
                        source.index("foreach ($name in $reportBytes.Keys)"))
        self.assertIn("$stream.Write($reportBytes[$name])", source)
        self.assertIn("[IO.FileMode]::CreateNew", source)
        self.assertIn("derived aggregate: 11 projects / 33 required cases", source)
        self.assertIn("CI execution and cleanup failures are preserved independently.", source)
        runner = (ROOT / "scripts/Test-Ui.ps1").read_text(encoding="utf-8")
        self.assertIn("$fixtureSql.set_Pooling($false)", runner)
        self.assertIn("[Cohors.Tests.SignalReviewBrowserFixture]::Create($fixtureSql.ConnectionString", runner)
        self.assertNotIn("ClearAllPools", runner)
        self.assertIn("An active connection remains in the exact owned database", runner)


if __name__ == "__main__":
    unittest.main()
