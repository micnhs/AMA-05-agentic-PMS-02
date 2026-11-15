import json
import shutil
import subprocess
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "Invoke-CohorsCi.ps1"
PROBE = r"""
$ErrorActionPreference = 'Stop'
$inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($inputData.Script, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Production harness has syntax errors.' }
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-CiSqlPassword'
}, $true))
if ($definitions.Count -ne 1) { throw 'Expected exactly one production SQL password extractor.' }
$definition = $definitions[0].Extent.Text
if ($inputData.Mutate) {
    $setter = '$connection.set_ConnectionString($ConnectionString)'
    if (-not $definition.Contains($setter)) { throw 'The old-assignment mutation no longer targets the CLR setter.' }
    $definition = $definition.Replace($setter, '$connection.ConnectionString = $ConnectionString')
}
. ([scriptblock]::Create($definition))
$calls = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Get-CiSqlPassword'
}, $true))
if ($calls.Count -ne 1) { throw 'Expected exactly one production extractor call.' }
$inventoryCall = $calls[0].Parent
while ($inventoryCall -and $inventoryCall -isnot [Management.Automation.Language.InvokeMemberExpressionAst]) {
    $inventoryCall = $inventoryCall.Parent
}
if (-not $inventoryCall -or $inventoryCall.Expression.Extent.Text -ne '$prohibitedValues' -or
    $inventoryCall.Member.Value -ne 'Add') {
    throw 'Expected the production extractor to populate the prohibited credential inventory.'
}
$extract = [scriptblock]::Create($inventoryCall.Extent.Text)
$results = foreach ($case in $inputData.Cases) {
    $settings = [pscustomobject]@{ ConnectionStrings = [pscustomobject]@{ Cohors = $case.ConnectionString } }
    $prohibitedValues = [System.Collections.Generic.List[string]]::new()
    try {
        & $extract
        [pscustomobject]@{
            Accepted = $true
            Count = $prohibitedValues.Count
            Exact = $prohibitedValues.Count -eq 1 -and $prohibitedValues[0] -ceq $case.Expected
            Error = ''
        }
    } catch {
        [pscustomobject]@{
            Accepted = $false
            Count = $prohibitedValues.Count
            Exact = $false
            Error = $_ | Out-String
        }
    }
}
ConvertTo-Json -InputObject @($results) -Depth 5 -Compress
"""


class CiCredentialTests(unittest.TestCase):
    def run_extractor(self, cases, mutate=False):
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell 7 is required; credential regression tests must not be skipped.")
        result = subprocess.run(
            [executable, "-NoProfile", "-NonInteractive", "-Command", PROBE],
            input=json.dumps({"Script": str(SCRIPT), "Cases": cases, "Mutate": mutate}),
            capture_output=True, text=True, encoding="utf-8", timeout=120,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stderr, "")
        return json.loads(result.stdout)

    def test_real_extraction_preserves_plain_quoted_and_case_insensitive_passwords(self):
        cases = [
            {"ConnectionString": "Server=127.0.0.1;Database=synthetic;User ID=ci;Password=Synthetic-Only_42!",
             "Expected": "Synthetic-Only_42!"},
            {"ConnectionString": 'Server=localhost;Database=synthetic;User ID=ci;Password="  Synthetic;Only=42""quoted  "',
             "Expected": '  Synthetic;Only=42"quoted  '},
            {"ConnectionString": "Server=localhost;Database=synthetic;User ID=ci;pAsSwOrD=Synthetic-Case_42!",
             "Expected": "Synthetic-Case_42!"},
        ]
        for result in self.run_extractor(cases):
            self.assertEqual(result, {"Accepted": True, "Count": 1, "Exact": True, "Error": ""})

    def test_invalid_credentials_never_enter_inventory_or_error_output(self):
        marker = "Synthetic-Only_DoNotEcho"
        values = [
            None, "", "   ", 42, {"Password": marker},
            "Server=localhost;Database=synthetic;User ID=ci",
            "Server=localhost;Password=",
            'Server=localhost;Password="   "',
            f'Server=localhost;Password="{marker}',
            f'Server=localhost;Password="{marker}\ncontinued"',
            f'Server=localhost;Password="{marker}\x00continued"',
        ]
        results = self.run_extractor([{"ConnectionString": value, "Expected": None} for value in values])
        self.assertEqual(len(results), len(values))
        for index, result in enumerate(results):
            with self.subTest(case=index):
                self.assertFalse(result["Accepted"])
                self.assertEqual(result["Count"], 0)
                self.assertIn("credential publication remains blocked.", result["Error"])
                self.assertNotIn(marker, result["Error"])

    def test_old_property_assignment_mutation_fails_the_real_extraction_path(self):
        case = {"ConnectionString": "Server=127.0.0.1;Database=synthetic;User ID=ci;Password=Synthetic-Mutation_42!",
                "Expected": "Synthetic-Mutation_42!"}
        self.assertTrue(self.run_extractor([case])[0]["Exact"])
        result = self.run_extractor([case], mutate=True)[0]
        self.assertFalse(result["Accepted"])
        self.assertEqual(result["Count"], 0)
        self.assertIn("Fresh SQL password is missing or invalid", result["Error"])
        self.assertNotIn(case["Expected"], result["Error"])


if __name__ == "__main__":
    unittest.main()
