param(
    [Parameter(Mandatory)][string]$TrxPath,
    [string]$JUnitPath
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) { throw 'SQL/.NET TRX report is missing.' }
[xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
$counters = $trx.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters -or [int]$counters.total -le 0 -or [int]$counters.executed -ne [int]$counters.total) {
    throw 'No complete .NET test execution: zero tests, missing counters or skipped tests.'
}
foreach ($name in @('failed', 'error', 'aborted', 'notExecuted', 'notRunnable', 'inconclusive')) {
    if ([int]$counters.GetAttribute($name) -gt 0) { throw ".NET report has non-passing tests ($name)." }
}
if ([int]$counters.passed -ne [int]$counters.total) { throw '.NET test report is not entirely passed.' }
Write-Output "SQL/.NET: $($counters.passed) executed and passed; none skipped."

if ($JUnitPath) {
    if (-not (Test-Path -LiteralPath $JUnitPath -PathType Leaf)) { throw 'Browser JUnit report is missing.' }
    [xml]$junit = Get-Content -LiteralPath $JUnitPath -Raw
    $cases = $junit.SelectNodes('//testcase')
    if ($cases.Count -eq 0 -or $junit.SelectNodes('//testcase/skipped | //testcase/failure | //testcase/error').Count -gt 0) {
        throw 'Browser report has zero, skipped or failing tests.'
    }
    Write-Output "Browser: $($cases.Count) executed and passed; none skipped."
}
