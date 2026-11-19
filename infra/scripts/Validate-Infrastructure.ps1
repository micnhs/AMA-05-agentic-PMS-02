param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Container', 'Local')][string]$SecurityScanner = 'Container'
)
$ErrorActionPreference = 'Stop'
$env:AZURE_CORE_COLLECT_TELEMETRY = 'no'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$scannerCommand = if ($SecurityScanner -eq 'Container') { 'docker' } else { 'checkov' }
foreach ($tool in @('az', 'python', 'actionlint', $scannerCommand)) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Missing validation tool: $tool. Install the documented pinned tool; this gate is not passed. The shared-network security deferral does not waive non-network or unclassified evidence."
    }
}
$checkovImage = 'bridgecrew/checkov@sha256:122602cf32cc72286155d39f1f234b7b594818f7b8d7fdf490701620e521368b'
$dockerOptions = @('run', '--rm', '--pull', 'never', '--network', 'none', '--read-only',
    '--user', '65532:65532', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
    '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m', '--env', 'HOME=/tmp',
    '--env', 'PYTHONDONTWRITEBYTECODE=1', '--env', 'LOG_LEVEL=ERROR')
$scannerArguments = @()
if ($SecurityScanner -eq 'Container') {
    $scannerOs = & docker image inspect $checkovImage --format '{{.Os}}'
    if ($LASTEXITCODE -ne 0 -or $scannerOs -ne 'linux') { throw 'The pinned Checkov Linux image must be preloaded; validation does not pull images or require network access.' }
    $scannerArguments = @($dockerOptions) + @($checkovImage)
}
$bicepVersion = ((& az bicep version) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $bicepVersion -notmatch 'version 0\.49\.2\b') { throw 'Bicep 0.49.2 is required.' }
$checkovVersion = ((& $scannerCommand @scannerArguments --version) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $checkovVersion -ne '3.4.5') { throw 'Checkov 3.4.5 is required.' }
$actionlintVersion = @(& actionlint -version)
if ($LASTEXITCODE -ne 0 -or $actionlintVersion[0] -ne '1.7.12') { throw 'Actionlint 1.7.12 is required.' }
Write-Output "$bicepVersion; Checkov $checkovVersion; Actionlint $($actionlintVersion[0])"

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $root) { throw 'Validation output must not be the repository root.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh validation directory; never overwrite earlier evidence.' }
New-Item -ItemType Directory -Path $output | Out-Null
$templates = @('main', 'primary-network', 'primary')
$checks = @()
foreach ($name in $templates) {
    & az bicep build --file (Join-Path $root "infra/$name.bicep") --outfile (Join-Path $output "$name.json")
    if ($LASTEXITCODE -ne 0) { throw "Bicep build failed: $name." }
    & az bicep lint --file (Join-Path $root "infra/$name.bicep")
    if ($LASTEXITCODE -ne 0) { throw "Bicep lint failed: $name." }
    Write-Output "Bicep $name build/lint: exit 0."
}
& az bicep build-params --file (Join-Path $root 'infra/parameters/pilot.example.bicepparam') `
    --outfile (Join-Path $output 'pilot.example.parameters.json')
if ($LASTEXITCODE -ne 0) { throw 'Bicep parameter compilation failed.' }
Write-Output 'Bicep parameter example: exit 0 (not deployable approval).'

foreach ($script in Get-ChildItem -LiteralPath (Join-Path $root 'infra/scripts') -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw "PowerShell syntax failed in $($script.Name): $($errors.Message -join '; ')" }
}
& actionlint (Join-Path $root '.github/workflows/cohors-ci.yml') (Join-Path $root '.github/workflows/cohors-deploy.yml') `
    (Join-Path $root '.github/workflows/cohors-primary-package.yml')
if ($LASTEXITCODE -ne 0) { throw 'Actionlint failed.' }
Write-Output 'PowerShell syntax and Actionlint: exit 0.'
$env:PYTHONDONTWRITEBYTECODE = '1'
& python -m unittest discover -s (Join-Path $root 'infra/tests') -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) { throw 'Offline gate and report tests failed.' }

foreach ($name in $templates) {
    $scanInput = Join-Path $output "checkov-$name-input"
    & python (Join-Path $PSScriptRoot 'checkov_templates.py') (Join-Path $output "$name.json") $scanInput
    if ($LASTEXITCODE -ne 0) { throw 'Checkov input preparation failed; no partial/zero-resource scan is accepted.' }
    $reportPath = Join-Path $output "checkov-$name.json"
    $diagnosticPath = Join-Path $output "checkov-$name.stderr.log"
    $observationPath = Join-Path $output "checkov-$name.executions.json"
    $scanDirectory = $scanInput
    $scanCommand = 'python'
    $scanArguments = @('-B', (Join-Path $PSScriptRoot 'checkov_observe.py'))
    if ($SecurityScanner -eq 'Container') {
        $scanCommand = 'docker'
        $scanArguments = @($dockerOptions) + @('--mount', "type=bind,source=$scanInput,target=/compiled,readonly",
            '--mount', "type=bind,source=$(Join-Path $PSScriptRoot 'checkov_observe.py'),target=/checkov_observe.py,readonly",
            '--entrypoint', 'python', $checkovImage, '-B', '/checkov_observe.py')
        $scanDirectory = '/compiled'
    }
    & $scanCommand @scanArguments -d $scanDirectory --framework arm --skip-download `
        --skip-results-upload --download-external-modules false --output json 2> $diagnosticPath |
        Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    $exitCode = $LASTEXITCODE
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $summary = $report.summary
    Write-Output "Checkov $name compiled ARM: $($summary | ConvertTo-Json -Compress); exit $exitCode."
    $diagnosticLines = @(Get-Content -LiteralPath $diagnosticPath)
    $marker = 'COHORS_CHECKOV_EXECUTION_OBSERVATION:'
    $observations = @($diagnosticLines | Where-Object { $_.StartsWith($marker) })
    if ($observations.Count -ne 1) { throw 'Missing or duplicate check-execution observation; no scanner pass.' }
    $observation = $observations[0].Substring($marker.Length) | ConvertFrom-Json
    $observation | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $observationPath -Encoding utf8NoBOM
    $diagnostics = ($diagnosticLines | Where-Object { -not $_.StartsWith($marker) }) -join "`n"
    Write-Output "Checkov $name Python executions: $($observation.counts | ConvertTo-Json -Compress)."
    $hasDiagnostics = (-not [string]::IsNullOrWhiteSpace($diagnostics)).ToString().ToLowerInvariant()
    $assessmentText = & python (Join-Path $PSScriptRoot 'checkov_observe.py') --assess $name `
        $reportPath $observationPath "$scanInput.manifest.json" $exitCode $hasDiagnostics
    if ($LASTEXITCODE -ne 0) { throw 'Scoped security assessment failed; no totals-only or unclassified waiver.' }
    $assessment = ($assessmentText -join "`n") | ConvertFrom-Json
    Write-Output "Checkov $name scoped review: rawPassed=$($assessment.passed); requiredChecksPassed=$($assessment.requiredChecksPassed); deferredNetworkExecutions=$($assessment.deferred.executed)."
    $checks += @{
        template = $name; passed = $assessment.passed; requiredChecksPassed = $assessment.requiredChecksPassed
        exitCode = $exitCode; summary = $summary; assessment = $assessment
        hasScannerDiagnostics = -not [string]::IsNullOrWhiteSpace($diagnostics)
        reportSha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
        diagnosticsSha256 = (Get-FileHash -LiteralPath $diagnosticPath -Algorithm SHA256).Hash.ToLowerInvariant()
        executionObservationSha256 = (Get-FileHash -LiteralPath $observationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        executionCounts = $observation.counts
        scanInputManifestSha256 = (Get-FileHash -LiteralPath "$scanInput.manifest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (-not [string]::IsNullOrWhiteSpace($diagnostics)) { Write-Output $diagnostics }
}
$gatePassed = @($checks | Where-Object { -not $_.passed }).Count -eq 0
$requiredChecksPassed = @($checks | Where-Object { -not $_.requiredChecksPassed }).Count -eq 0
@{ contract = 'cohors-infrastructure-security-gate-2'; passed = $gatePassed
    requiredChecksPassed = $requiredChecksPassed; checks = $checks } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'security-gate.json') -Encoding utf8NoBOM
if (-not $requiredChecksPassed) {
    throw "Required security gate is NOT passed. Review all three original reports, execution records and scoped assessments in $output. Non-network, out-of-scope and unclassified failures remain blocking."
}
if (-not $gatePassed) {
    Write-Warning 'Shared private-network security review is DEFERRED, not passed. Original failed/unknown results remain intact; this is not a global security pass.'
}
Write-Output 'Required local infrastructure checks passed under the explicit scoped review policy. No Azure login, deployment, what-if, application integration or restore was executed.'
