$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:GITHUB_REPOSITORY -cne 'micnhs/AMA-05-agentic-PMS-02' -or
    $env:GITHUB_REF -cne 'refs/heads/micnhs-cohors-fabric-umsetzung' -or $env:GITHUB_EVENT_NAME -cne 'push') {
    throw 'C1 packaging requires its explicitly requested trusted-branch hosted run. No local or PR fallback.'
}
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$sha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -cne $env:GITHUB_SHA) { throw 'Package source differs from the event source.' }
$message = (git --no-pager log -1 --format=%B) -join "`n"
if ($LASTEXITCODE -ne 0 -or
    [regex]::Matches($message, '(?m)^Cohors-C1-Package: requested\r?$').Count -ne 1) {
    throw 'Exactly one explicit Cohors-C1-Package: requested commit trailer is required.'
}
$temporary = Join-Path $env:RUNNER_TEMP "cohors-fabric-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$receiptPath = Join-Path $temporary 'validation.json'
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.sourceCommit -cne $sha -or $receipt.runId -cne $env:GITHUB_RUN_ID -or
    $receipt.runAttempt -cne $env:GITHUB_RUN_ATTEMPT -or $receipt.sourceContractsPassed -ne $true -or
    (dotnet --version).Trim() -cne '10.0.404') {
    throw 'This exact source/run/SDK must finish the existing validation before packaging.'
}
$publish = Join-Path $temporary 'publish'
$package = Join-Path $temporary 'package'
$verification = Join-Path $temporary 'package-verification'
if ((Test-Path -LiteralPath $publish) -or (Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $verification)) {
    throw 'Package paths already exist; do not overwrite or automatically retry an upload.'
}
New-Item -ItemType Directory -Path $publish, $package | Out-Null
$project = Join-Path $root 'src/Cohors.Api/Cohors.Api.csproj'
foreach ($rid in @('win-x64', 'linux-x64')) {
    $output = Join-Path $publish $rid
    $log = Join-Path $temporary "publish-$rid.log"
    $publishArguments = @('publish', $project, '--configuration', 'Release', '--runtime', $rid,
        '--self-contained', 'true', '--no-restore', '--disable-build-servers', '--output', $output,
        '-m:1', '-p:UseSharedCompilation=false', '-p:ContinuousIntegrationBuild=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '--verbosity', 'minimal')
    & dotnet @publishArguments *> $log
    if ($LASTEXITCODE -ne 0) {
        if (-not (Select-String -LiteralPath $log -Pattern 'NETSDK1004|NETSDK1047|NETSDK1112' -Quiet)) {
            Get-Content -LiteralPath $log -Tail 60
            throw "Cross-RID publish failed without a missing-assets/runtime-pack condition: $rid"
        }
        dotnet restore $project --runtime $rid --locked-mode -p:SelfContained=true --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Locked $rid restore failed; no unpinned dependency fallback." }
        & dotnet @publishArguments *> $log
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $log -Tail 60
            throw "Cross-RID publish failed: $rid"
        }
    }
}
git --no-pager diff --quiet --exit-code
if ($LASTEXITCODE -ne 0) { throw 'Validation/publishing changed tracked source or lockfiles; refuse a falsely SHA-bound artifact.' }
python3 scripts/fabric/c1_package.py build --root $root --publish-root $publish --destination $package --verification-directory $verification --source-commit $sha --run-id $env:GITHUB_RUN_ID --run-attempt $env:GITHUB_RUN_ATTEMPT
if ($LASTEXITCODE -ne 0) { throw 'Bounded payload allowlist/hash/credential verification failed. Nothing may be uploaded.' }
$published = Join-Path $verification 'runtimes/linux-x64/Cohors.Api'
$planJson = & $published --fabric-c1-deploy plan --manifest (Join-Path $verification 'assets/fabric/c1/deployment.template.json')
if ($LASTEXITCODE -ne 2 -or ($planJson | ConvertFrom-Json).status -cne 'Blocked') {
    throw 'The packaged Linux entry did not retain the offline missing-bindings refusal.'
}
$archive = Get-Content -LiteralPath (Join-Path $package 'archive.json') -Raw | ConvertFrom-Json
@(
    '## Explicit C1 deployment artifact request'
    "- Source/run/attempt: $sha / $env:GITHUB_RUN_ID / $env:GITHUB_RUN_ATTEMPT"
    "- Inner archive SHA256: $($archive.archiveSha256); bytes: $($archive.archiveLength)"
    "- File manifest SHA256: $($archive.manifestSha256)"
    '- self-contained win-x64/linux-x64 plus exact source-asset allowlist; one-day retention, at most 256 MiB compressed.'
    '- Cross-RID publish is not native Windows execution, a private route, a deployment or C1 service acceptance.'
) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
"package-directory=$package" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
