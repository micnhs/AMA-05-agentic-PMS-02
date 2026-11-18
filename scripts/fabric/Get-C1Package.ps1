#requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (-not [IO.Path]::IsPathFullyQualified($Destination) -or (Test-Path -LiteralPath $Destination)) {
    throw 'Use a new absolute destination; existing files are never replaced.'
}
$parent = Split-Path $Destination -Parent
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'The destination parent must already exist.' }
$ancestor = Get-Item -LiteralPath $parent -Force
while ($null -ne $ancestor) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Artifact destination ancestors cannot be links or reparse points.' }
    $ancestor = $ancestor.Parent
}
$repo = 'micnhs/AMA-05-agentic-PMS-02'
$runJson = gh api --hostname github.com "repos/$repo/actions/runs/$RunId"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the requested run with this GitHub account; no alternate account or login.' }
$run = $runJson | ConvertFrom-Json
if ($run.id.ToString() -cne $RunId -or $run.head_sha -cne $SourceCommit -or
    $run.run_attempt.ToString() -cnotmatch '^[1-9][0-9]*$' -or
    $run.head_branch -cne 'micnhs-cohors-fabric-umsetzung' -or
    $run.repository.full_name -cne $repo -or $run.head_repository.full_name -cne $repo -or
    $run.path -cne '.github/workflows/cohors-fabric-contracts.yml' -or $run.event -cne 'push' -or
    $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
    throw 'Source/repository/branch/workflow/event or successful-run provenance differs.'
}
$listJson = gh api --hostname github.com "repos/$repo/actions/runs/$RunId/artifacts?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read this run artifact inventory.' }
$list = $listJson | ConvertFrom-Json
if ($list.total_count -ne @($list.artifacts).Count) { throw 'Artifact inventory is incomplete; no inferred selection.' }
$name = "cohors-fabric-c1-$RunId-$($run.run_attempt)"
$selected = @($list.artifacts | Where-Object { $_.name -ceq $name })
if ($selected.Count -ne 1) { throw 'No unique artifact for this exact run attempt.' }
$artifact = $selected[0]
if ($artifact.expired -ne $false -or [DateTimeOffset]::Parse($artifact.expires_at) -le [DateTimeOffset]::UtcNow -or
    $artifact.id.ToString() -cnotmatch '^[1-9][0-9]*$' -or
    $artifact.size_in_bytes -le 0 -or $artifact.size_in_bytes -gt 256MB -or
    $artifact.digest -cnotmatch '^sha256:[0-9a-f]{64}$' -or
    $artifact.workflow_run.head_sha -cne $SourceCommit -or $artifact.workflow_run.id.ToString() -cne $RunId) {
    throw 'Artifact expiry, digest, size or source/run binding is invalid.'
}
$download = Join-Path $parent ('.c1-artifact-' + [Guid]::NewGuid().ToString('N') + '.zip')
$start = [Diagnostics.ProcessStartInfo]::new((Get-Command gh -CommandType Application).Source)
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($arg in @('api', '--hostname', 'github.com', "repos/$repo/actions/artifacts/$($artifact.id)/zip")) {
    $start.ArgumentList.Add($arg)
}
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
try {
    $stream = [IO.File]::Open($download, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(180))
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Artifact download process did not start.' }
        $errorRead = $process.StandardError.ReadToEndAsync()
        $buffer = [byte[]]::new(65536)
        while (($read = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($stream.Length + $read -gt $artifact.size_in_bytes -or $stream.Length + $read -gt 256MB) {
                throw 'Artifact stream exceeded its bound before writing.'
            }
            $stream.Write($buffer, 0, $read)
        }
        $process.WaitForExitAsync($timeout.Token).GetAwaiter().GetResult()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Authenticated artifact download failed; no retry or credential fallback.' }
        if ($stream.Length -ne $artifact.size_in_bytes -or $stream.Length -gt 256MB) { throw 'Downloaded archive length differs.' }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true) }
        $timeout.Dispose()
        $stream.Dispose()
    }
    $python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $python) { $python = Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1 }
    $verifiedJson = & $python.Source (Join-Path $PSScriptRoot 'c1_package.py') receive --archive $download --outer-sha256 $artifact.digest.Substring(7) --source-commit $SourceCommit --run-id $RunId --run-attempt $run.run_attempt --destination $Destination
    if ($LASTEXITCODE -ne 0) { throw 'Artifact content/provenance/path verification failed. No payload was executed.' }
    $verified = $verifiedJson | ConvertFrom-Json
    $provenance = [ordered]@{
        contract = 'cohors-fabric-c1-download-1'
        status = $verified.status
        repository = $repo
        branch = $run.head_branch
        workflow = $run.path
        sourceCommit = $SourceCommit
        runId = $RunId
        runAttempt = $run.run_attempt.ToString()
        artifactId = $artifact.id.ToString()
        artifactName = $artifact.name
        outerArchiveSha256 = $artifact.digest.Substring(7)
        outerArchiveLength = $artifact.size_in_bytes
        innerArchiveSha256 = $verified.archiveSha256
        expiresAtUtc = $artifact.expires_at
    }
    $provenancePath = Join-Path $Destination 'download-provenance.json'
    $provenanceFile = [IO.File]::Open($provenancePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($provenance | ConvertTo-Json -Compress))
        $provenanceFile.Write($bytes, 0, $bytes.Length)
    } finally { $provenanceFile.Dispose() }
    $provenance | ConvertTo-Json -Compress
} finally {
    $process.Dispose()
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
}
