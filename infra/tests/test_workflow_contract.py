import ast
import copy
import datetime as dt
import json
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]


class WorkflowContractTests(unittest.TestCase):
    def diagnostic_helpers(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-recovery-copy-diagnostic.yml").read_text(encoding="utf-8"))
        body = next(step["run"] for step in workflow["jobs"]["diagnostic"]["steps"] if "run" in step)
        code = body.split("python3 - <<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]
        tree = ast.parse(code)
        compile(tree, "hosted-diagnostic-owner", "exec")
        helpers = ast.Module(body=[node for node in tree.body if isinstance(node, ast.FunctionDef)
                                  and node.name in ("execution_in_window", "validate_core_result",
                                                    "validate_backup_access", "backup_access_complete")], type_ignores=[])
        namespace = {"re": re, "dt": dt}
        exec(compile(helpers, "offline-diagnostic-helpers", "exec"), namespace)
        return namespace

    def test_diagnostic_rejects_late_exit_late_observation_and_unbound_execution(self):
        within = self.diagnostic_helpers()["execution_in_window"]
        end = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
        earlier = end - dt.timedelta(seconds=1)
        self.assertTrue(within(1000, 180000001000, 179000000, 179500000000, end, earlier, True, False))
        for exited, observed, clock, admitted, backward in (
                (180000002, 180000003000, earlier, True, False),
                (179000000, 180000002000, earlier, True, False),
                (179000000, 179500000000, end, True, False),
                (179000000, 179500000000, earlier, False, False),
                (179000000, 179500000000, earlier, True, True)):
            with self.subTest(exited=exited, observed=observed, admitted=admitted, backward=backward):
                self.assertFalse(within(1000, 180000001000, exited, observed, end, clock, admitted, backward))

    def test_diagnostic_artifact_rejects_unknown_content_types_and_acceptance_claims(self):
        validate = self.diagnostic_helpers()["validate_core_result"]
        digest, image = "a" * 64, "synthetic-pinned-image"
        minimal_failure = {"applicationAcceptance": False, "cloudRelease": False, "scriptSha256": digest, "image": image}
        self.assertEqual(validate(minimal_failure, digest, image), minimal_failure)
        for mutation in ({"rawOutput": "not publishable"}, {"sqlErrors": ["3201"]},
                         {"sqlErrors": [True]}, {"verifyOnlyExitCode": False},
                         {"applicationAcceptance": True}, {"scriptSha256": "b" * 64},
                         {"hostMode": "1001:128:640:123\nadditional output"}):
            with self.subTest(keys=list(mutation)):
                with self.assertRaises(RuntimeError):
                    validate(minimal_failure | mutation, digest, image)

    def repair_fixture(self):
        hashes = {role: {"publishedSha256": letter * 64}
                  for role, letter in (("extension", "a"), ("backup", "b"), ("restore", "c"))}
        negative = {"exitCode": 1, "sqlErrors": [3201, 3013], "operatingSystemErrors": [5]}
        core = {
            "applicationAcceptance": False, "cloudRelease": False, "scriptSha256": "e" * 64,
            "image": "synthetic-pinned-image", "permissionFailureReproduced": True,
            "sqlUserCanRead": False, "verifyOnlyPassed": False, "verifyOnlyExitCode": 1,
            "sqlErrors": [3201, 3013], "operatingSystemErrors": [5],
            "sqlUser": "10001", "sqlGroup": "10001", "sqlProcessUidGid": "10001 10001\n10001 10001",
            "sourceMode": "10001:10001:640:123", "privateDirectoryMode": "10001:10001:700",
            "targetMode": "1001:1001:640:123", "hostMode": "1001:1001:640:123",
            "nativeBackupSha256": "d" * 64, "startedUtc": "2026-01-01T00:00:00.000000Z",
            "executionDeadlineUtc": "2026-01-01T00:03:00.000000Z",
            "observedAtUtc": "2026-01-01T00:00:30.000000Z",
            "backupAccessRepair": {
                "contract": "cohors-hosted-linux-backup-access-1", "passed": True,
                "applicationAcceptance": False, "recoveryAcceptance": False, "cloudRelease": False,
                "extensionSha256": "a" * 64, "backupScriptSha256": "b" * 64, "restoreScriptSha256": "c" * 64,
                "productionFunction": "Set-RecoverySqlBackupAccess",
                "transport": "Original bounded Linux diagnostic Docker transport; production function body is unchanged.",
                "sameOriginal180SecondWindow": True, "separateContainerCreated": False,
                "originalNegativeObservationPreserved": True, "rawOutputPublished": False,
                "scopeRefusals": 4, "nativeLinkRefusals": 3, "ownedTransientFilesRemoved": True,
                "sqlUserCanReadAfter": True, "otherUnprivilegedUserCannotReadAfter": True,
                "beforeMode": "1001:1001:640:123", "afterMode": "10001:10001:600:123",
                "directoryMode": "10001:10001:700", "hostModeAfter": "1001:1001:640:123",
                "beforeSha256": "d" * 64, "afterSha256": "d" * 64, "hostSha256After": "d" * 64,
                "beforeVerification": negative,
                "afterVerification": {"exitCode": 0, "sqlErrors": [], "operatingSystemErrors": []},
                "observedAtUtc": "2026-01-01T00:00:29.000000Z"}}
        return core, hashes

    def test_repair_nested_artifact_still_rejects_raw_data_wrong_sources_and_acceptance(self):
        core, hashes = self.repair_fixture()
        validate = self.diagnostic_helpers()["validate_core_result"]
        self.assertEqual(validate(core, core["scriptSha256"], core["image"], hashes), core)
        for mutation in ({"rawOutput": "not publishable"}, {"backupScriptSha256": "f" * 64},
                         {"recoveryAcceptance": True}, {"separateContainerCreated": True},
                         {"scopeRefusals": True}, {"failurePhase": "unbounded raw error"},
                         {"afterVerification": {"exitCode": 0, "sqlErrors": [], "operatingSystemErrors": [], "token": "secret"}},
                         {"beforeVerification": {"exitCode": 1, "sqlErrors": ["3201"], "operatingSystemErrors": [5]}}):
            with self.subTest(keys=list(mutation)), self.assertRaises(RuntimeError):
                changed = copy.deepcopy(core)
                changed["backupAccessRepair"].update(mutation)
                validate(changed, core["scriptSha256"], core["image"], hashes)
        with self.assertRaises(RuntimeError):
            validate(core, core["scriptSha256"], core["image"])
        for key in ("contract", "productionFunction", "transport"):
            with self.subTest(missing=key), self.assertRaises(RuntimeError):
                changed = copy.deepcopy(core)
                del changed["backupAccessRepair"][key]
                validate(changed, core["scriptSha256"], core["image"], hashes)

    def test_repair_pass_needs_native_before_after_denials_bytes_and_original_deadline(self):
        core, _ = self.repair_fixture()
        complete = self.diagnostic_helpers()["backup_access_complete"]
        self.assertTrue(complete(core))
        for mutation in ({"passed": False}, {"afterMode": "10001:10001:644:123"},
                         {"afterSha256": "f" * 64}, {"hostModeAfter": "10001:10001:600:123"},
                         {"beforeVerification": {"exitCode": 0, "sqlErrors": [], "operatingSystemErrors": []}},
                         {"afterVerification": {"exitCode": 1, "sqlErrors": [3013], "operatingSystemErrors": []}},
                         {"scopeRefusals": 3}, {"nativeLinkRefusals": 2}, {"ownedTransientFilesRemoved": False},
                         {"observedAtUtc": "2026-01-01T00:03:00.000000Z"},
                         {"sameOriginal180SecondWindow": False}, {"originalNegativeObservationPreserved": False},
                         {"failureType": "InvalidOperationException"}):
            with self.subTest(keys=list(mutation)):
                changed = copy.deepcopy(core)
                changed["backupAccessRepair"].update(mutation)
                self.assertFalse(complete(changed))
        for key in ("backupAccessRepair", "targetMode", "executionDeadlineUtc"):
            with self.subTest(missing=key):
                changed = copy.deepcopy(core)
                del changed[key]
                self.assertFalse(complete(changed))
        self.assertFalse(complete(core | {"permissionFailureReproduced": False}))
        self.assertFalse(complete(core | {"operatingSystemErrors": [13]}))
        self.assertFalse(complete(core | {"sqlProcessUidGid": "0 0"}))

    def test_extension_invocation_shares_the_original_owner_scope_and_deadline(self):
        harness = r"""
#requires -Version 7.6
param([string]$CorePath, [string]$ExtensionPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Parse-Source([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Diagnostic source must parse.' }
    return $ast
}
$core = Parse-Source $CorePath
$extension = Parse-Source $ExtensionPath
$guard = $core.Find({ param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $n.Name -ceq 'Assert-DiagnosticDeadline'
}, $true)
$assignment = $core.Find({ param($n)
    $n -is [Management.Automation.Language.AssignmentStatementAst] -and
        $n.Left.Extent.Text -ceq '$result.backupAccessRepair' -and
        $n.Right.Extent.Text.Contains("'recovery-backup-access.ps1'")
}, $true)
$entry = @($extension.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.PipelineAst] -and
        $_.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
        $_.PipelineElements[0].GetCommandName() -ceq 'Assert-DiagnosticDeadline'
})
if ($null -eq $guard -or $null -eq $assignment -or $entry.Count -ne 1) {
    throw 'The real caller, parameter contract and first deadline guard must be identifiable.'
}
# The source-derived child exercises the actual call/parameter boundary without platform spoofing or transport.
$child = $extension.ParamBlock.Extent.Text + "`n" + $entry[0].Extent.Text + "`n" + @'
[ordered]@{
    deadlineTicks = $script:deadline.UtcTicks
    observationTicks = $script:lastObservation.UtcTicks
    bindingPreserved = $Baseline.owner -ceq $OwnerId -and
        $ContainerId -ceq ('a' * 64) -and $EvidenceDirectory -ceq $script:evidence
}
'@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'recovery-backup-access.ps1'), $child)
$actual = $assignment.Extent.Text
$legacy = [regex]::new('^(\$result\.backupAccessRepair\s*=\s*)[.&](?=\s)').Replace($actual, '$1&', 1)
$parent = @'
param([string]$Case)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
__GUARD__
$started = [DateTimeOffset]::UtcNow
$originalDeadline = $started.AddSeconds(180)
$container = 'a' * 64
$owner = 'b' * 32
$evidence = $PSScriptRoot
$script:deadline = if ($Case -ceq 'expired') { $started.AddSeconds(-1) } else { $originalDeadline }
$script:lastObservation = if ($Case -ceq 'rollback') { $originalDeadline } else { $started }
$result = [ordered]@{ owner = $owner }
$check = [ordered]@{ succeeded = $false }
try {
    __CALL__
    $check.succeeded = $true
    $check.sameDeadline = $result.backupAccessRepair.deadlineTicks -eq $originalDeadline.UtcTicks -and
        $script:deadline -eq $originalDeadline
    $check.sharedObservation = $result.backupAccessRepair.observationTicks -eq $script:lastObservation.UtcTicks -and
        $script:lastObservation -ge $started
    $check.bindingPreserved = $result.backupAccessRepair.bindingPreserved
} catch {
    $check.errorId = $_.FullyQualifiedErrorId
    $check.undefinedOwnerObservation = $_.Exception.Message.Contains('script:lastObservation')
    $check.originalDeadlineRejected = $_.Exception.Message.StartsWith('diagnostic_deadline:')
}
$check
'@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'current-owner.ps1'),
    $parent.Replace('__GUARD__', $guard.Extent.Text).Replace('__CALL__', $actual))
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'legacy-owner.ps1'),
    $parent.Replace('__GUARD__', $guard.Extent.Text).Replace('__CALL__', $legacy))
$checks = [ordered]@{}
foreach ($case in @('legacy', 'current', 'expired', 'rollback')) {
    $file = if ($case -ceq 'legacy') { 'legacy-owner.ps1' } else { 'current-owner.ps1' }
    $checks[$case] = & (Join-Path $PSScriptRoot $file) -Case $case
}
$checks | ConvertTo-Json -Depth 4 -Compress
"""
        with tempfile.TemporaryDirectory(prefix="cohors-scope-contract-") as directory:
            path = Path(directory) / "owner.ps1"
            path.write_text(harness, encoding="utf-8")
            run = subprocess.run(
                ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(path),
                 "-CorePath", str(ROOT / "diagnostics/recovery-host-copy.ps1"),
                 "-ExtensionPath", str(ROOT / "diagnostics/recovery-backup-access.ps1")],
                capture_output=True, text=True, timeout=30, check=True)
            observed = json.loads(run.stdout)
        self.assertFalse(observed["legacy"]["succeeded"])
        self.assertEqual(observed["legacy"]["errorId"].split(",")[0], "VariableIsUndefined")
        self.assertTrue(observed["legacy"]["undefinedOwnerObservation"])
        self.assertEqual(observed["current"], {
            "succeeded": True, "sameDeadline": True, "sharedObservation": True, "bindingPreserved": True})
        for case in ("expired", "rollback"):
            self.assertFalse(observed[case]["succeeded"])
            self.assertTrue(observed[case]["originalDeadlineRejected"])

    def test_recovery_diagnostic_is_a_narrow_read_only_push_workflow(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-recovery-copy-diagnostic.yml").read_text(encoding="utf-8"))
        self.assertEqual(workflow["on"], {"push": {
            "branches": ["micnhs-cohors-azure-infrastruktur"],
            "paths": [".github/workflows/cohors-recovery-copy-diagnostic.yml", "diagnostics/recovery-host-copy.ps1",
                      "diagnostics/recovery-backup-access.ps1", "diagnostics/recovery-source-1e8acd4/Backup-Local.ps1",
                      "diagnostics/recovery-source-1e8acd4/Restore-Local.ps1"]}})
        self.assertEqual(workflow["permissions"], {"contents": "read"})
        self.assertFalse(workflow["concurrency"]["cancel-in-progress"])
        self.assertEqual(set(workflow["jobs"]), {"diagnostic"})
        job = workflow["jobs"]["diagnostic"]
        self.assertEqual(job["runs-on"], "ubuntu-24.04")
        self.assertNotIn("permissions", job)
        package = yaml.safe_load((ROOT / ".github/workflows/cohors-primary-package.yml").read_text(encoding="utf-8"))
        self.assertEqual(package["on"]["push"]["paths"],
                         ["infra/**", "!infra/tests/test_workflow_contract.py", ".github/workflows/cohors-primary-package.yml"])

    def test_infrastructure_binds_existing_acceptance_instead_of_repeating_it(self):
        ci = yaml.safe_load((ROOT / ".github/workflows/cohors-ci.yml").read_text(encoding="utf-8"))
        app = next(step for step in ci["jobs"]["acceptance"]["steps"]
                   if step.get("with", {}).get("path") == "application")
        self.assertEqual(app["with"]["ref"], "${{ github.sha }}")
        self.assertEqual(ci["jobs"]["acceptance"]["if"],
                         "${{ (github.head_ref || github.ref_name) != 'micnhs-cohors-azure-infrastruktur' }}")
        existing = ci["jobs"]["application-source-binding"]
        self.assertEqual(existing["if"],
                         "${{ (github.head_ref || github.ref_name) == 'micnhs-cohors-azure-infrastruktur' }}")
        command = next(step["run"] for step in existing["steps"] if "run" in step)
        self.assertIn("--commit 88b1904012c26db83b3d00faf147157b68899d88", command)
        self.assertIn("--run-id 44528532161", command)
        self.assertNotRegex(command, r"dotnet|Invoke-CohorsCi|gh run rerun|workflow run")

    def test_cloud_credentials_are_only_in_protected_manual_jobs(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/cohors-deploy.yml").read_text(encoding="utf-8"))
        self.assertEqual(set(workflow["on"]), {"workflow_dispatch"})
        self.assertNotIn("id-token", workflow["permissions"])
        for name, job in workflow["jobs"].items():
            if job.get("permissions", {}).get("id-token") == "write":
                self.assertIn(job["environment"], {"cohors-pilot-preview", "cohors-pilot-deploy"})
                self.assertIn(name, {"preview", "apply"})
        self.assertEqual(workflow["jobs"]["apply"]["needs"], "preview")
        self.assertIn("inputs.operation == 'apply'", workflow["jobs"]["apply"]["if"])
        ci = yaml.safe_load((ROOT / ".github/workflows/cohors-ci.yml").read_text(encoding="utf-8"))
        self.assertNotIn("id-token", ci["permissions"])
        self.assertTrue(all("id-token" not in job.get("permissions", {}) for job in ci["jobs"].values()))

    def test_actions_are_immutable_and_artifacts_do_not_include_credentials(self):
        for path in (ROOT / ".github/workflows").glob("cohors-*.yml"):
            workflow = yaml.safe_load(path.read_text(encoding="utf-8"))
            for job in workflow["jobs"].values():
                for step in job["steps"]:
                    if "uses" in step:
                        self.assertRegex(step["uses"], r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$")
                    if step.get("uses", "").startswith("actions/upload-artifact@"):
                        paths = step["with"]["path"].splitlines()
                        if path.name == "cohors-recovery-copy-diagnostic.yml":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-recovery-copy-result.json"])
                            self.assertEqual(step["with"]["name"], "cohors-recovery-copy-diagnostic")
                            self.assertEqual(step["with"]["retention-days"], 1)
                            self.assertEqual(step["with"]["if-no-files-found"], "error")
                        elif path.name == "cohors-primary-package.yml":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-primary-package/cohors-primary-release.zip"])
                            self.assertNotIn("id-token", workflow["permissions"])
                        elif step["with"].get("name") == "cohors-infrastructure-evidence":
                            self.assertEqual(set(paths), {
                                "${{ runner.temp }}/cohors-iac/" + name for name in (
                                    "main.json", "primary-network.json", "primary.json",
                                    "checkov-main.json", "checkov-primary-network.json", "checkov-primary.json",
                                    "checkov-main.stderr.log", "checkov-primary-network.stderr.log",
                                    "checkov-main.executions.json", "checkov-primary-network.executions.json",
                                    "checkov-primary.executions.json", "checkov-main-input.manifest.json",
                                    "checkov-primary-network-input.manifest.json", "checkov-primary-input.manifest.json",
                                    "checkov-primary.stderr.log", "security-gate.json")})
                        elif step["with"].get("name") == "cohors-existing-application-acceptance":
                            self.assertEqual(paths, ["${{ runner.temp }}/cohors-application-acceptance.json"])
                        else:
                            self.assertEqual(len(paths), 4)
                            self.assertTrue(all(re.search(r"/(acceptance\.trx|e2e-results\.xml|cohors-cockpit\.png|cohors-workbench\.png)$", item) for item in paths))
                        self.assertNotRegex(step["with"]["path"], r"settings|demo-access|trace|storageState|\*\*")


if __name__ == "__main__":
    unittest.main()
