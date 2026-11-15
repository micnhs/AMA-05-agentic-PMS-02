param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$env:AZURE_CORE_COLLECT_TELEMETRY = 'no'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
foreach ($tool in @('az', 'python', 'docker', 'actionlint')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Missing validation tool: $tool. Install the documented pinned tool; this gate is not passed."
    }
}
$checkovImage = 'bridgecrew/checkov@sha256:122602cf32cc72286155d39f1f234b7b594818f7b8d7fdf490701620e521368b'
$dockerOptions = @('run', '--rm', '--pull', 'never', '--network', 'none', '--read-only',
    '--user', '65532:65532', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
    '--tmpfs', '/tmp:rw,noexec,nosuid,size=512m', '--env', 'HOME=/tmp',
    '--env', 'PYTHONDONTWRITEBYTECODE=1', '--env', 'LOG_LEVEL=ERROR')
$scannerOs = & docker image inspect $checkovImage --format '{{.Os}}'
if ($LASTEXITCODE -ne 0 -or $scannerOs -ne 'linux') { throw 'The pinned Checkov Linux image must be preloaded; validation does not pull images or require network access.' }
$bicepVersion = ((& az bicep version) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $bicepVersion -notmatch 'version 0\.49\.2\b') { throw 'Bicep 0.49.2 is required.' }
$checkovVersion = ((& docker @dockerOptions $checkovImage --version) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $checkovVersion -ne '3.4.5') { throw 'Checkov 3.4.5 is required.' }
$actionlintVersion = @(& actionlint -version)
if ($LASTEXITCODE -ne 0 -or $actionlintVersion[0] -ne '1.7.12') { throw 'Actionlint 1.7.12 is required.' }
Write-Output "$bicepVersion; Checkov $checkovVersion; Actionlint $($actionlintVersion[0])"

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $root) { throw 'Validation output must not be the repository root.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$template = Join-Path $output 'main.json'
& az bicep build --file (Join-Path $root 'infra/main.bicep') --outfile $template
if ($LASTEXITCODE -ne 0) { throw 'Bicep build failed.' }
Write-Output 'Bicep build: exit 0.'
& az bicep lint --file (Join-Path $root 'infra/main.bicep')
if ($LASTEXITCODE -ne 0) { throw 'Bicep lint failed.' }
Write-Output 'Bicep lint: exit 0.'
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
& actionlint (Join-Path $root '.github/workflows/cohors-ci.yml') (Join-Path $root '.github/workflows/cohors-deploy.yml')
if ($LASTEXITCODE -ne 0) { throw 'Actionlint failed.' }
Write-Output 'PowerShell syntax and Actionlint: exit 0.'
$env:PYTHONDONTWRITEBYTECODE = '1'
& python -m unittest discover -s (Join-Path $root 'infra/tests') -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) { throw 'Offline gate and report tests failed.' }

$scanInput = Join-Path $output "checkov-input-$([Guid]::NewGuid().ToString('N'))"
& python (Join-Path $PSScriptRoot 'checkov_templates.py') $template $scanInput
if ($LASTEXITCODE -ne 0) { throw 'Checkov input preparation failed; no partial/zero-resource scan is accepted.' }
$reportPath = Join-Path $output 'checkov-arm.json'
& docker @dockerOptions --mount "type=bind,source=$scanInput,target=/compiled,readonly" $checkovImage `
    -d /compiled --framework arm --skip-download --skip-results-upload --download-external-modules false --output json |
    Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
$exitCode = $LASTEXITCODE
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$summary = $report.summary
Write-Output "Checkov compiled ARM: $($summary | ConvertTo-Json -Compress); exit $exitCode."
if ($exitCode -ne 0 -or $summary.failed -ne 0 -or $summary.parsing_errors -ne 0 -or
    $summary.skipped -ne 0 -or [int]$summary.passed -le 0 -or [int]$summary.resource_count -le 0) {
    throw "Security gate is NOT passed. Review $reportPath and the per-control classification in docs/azure-infrastructure.md; no findings are automatically waived."
}
Write-Output 'Local infrastructure gate passed. No Azure login, deployment, what-if, application integration or restore was executed.'
