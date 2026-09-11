"""Exercise the onboarding parser without launching or installing Unreal."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("powershell.exe") or shutil.which("pwsh")


@unittest.skipUnless(POWERSHELL, "PowerShell is required for onboarding parser tests")
class AutomationEvidenceTests(unittest.TestCase):
    def test_duplicate_logs_failures_and_engine_errors(self):
        success = "LogAutomationController: Display: Test Completed. Result={Success} Name={Test} Path={Finare.Unreal.Test}"
        failure = success.replace("{Success}", "{Fail}")
        cases = [success + "\n" + success, failure + "\n" + success,
                 success + "\nLogOutputDevice: Error: Ensure condition failed: WorldReleased",
                 success + "\nFatal error: World not released", "Editor exited with code 0"]
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory) / "cases.json"
            data.write_text(json.dumps(cases), encoding="utf-8")
            env = os.environ | {"FINARE_TEST_ROOT": str(ROOT), "FINARE_TEST_CASES": str(data)}
            # Parse, then load only this pure function. The onboarding script's
            # install/build side effects must never run in a tooling unit test.
            command = r'''
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $env:FINARE_TEST_ROOT 'tools/setup-unreal-project.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
$functionNode = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-FinareAutomationEvidence' }, $true)
if ($null -eq $functionNode) { throw 'Automation evidence function is missing' }
Invoke-Expression $functionNode.Extent.Text
$caseValues = Get-Content -LiteralPath $env:FINARE_TEST_CASES -Raw | ConvertFrom-Json
$results = @(foreach ($caseText in $caseValues) {
    Get-FinareAutomationEvidence $caseText
})
ConvertTo-Json -InputObject $results -Compress
'''
            completed = subprocess.run([POWERSHELL, "-NoProfile", "-NonInteractive", "-Command", command],
                                       env=env, text=True, capture_output=True, check=True)
            rows = json.loads(completed.stdout)
        self.assertEqual(rows[0], {"passed": 1, "failed": 0, "criticalFailures": 0})
        self.assertEqual(rows[1]["failed"], 1, "later success cannot erase a failure")
        self.assertEqual(rows[2]["criticalFailures"], 1)
        self.assertEqual(rows[3]["criticalFailures"], 1)
        self.assertEqual(rows[4]["passed"], 0, "process exit is not an executed test")


if __name__ == "__main__":
    unittest.main()
