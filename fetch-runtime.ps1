[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $root 'runtime.lock.json') -Raw | ConvertFrom-Json
if ($pin.repository -cne 'theatrus/psf-guard' -or $pin.commit -cnotmatch '^[a-f0-9]{40}$' -or
    $pin.executable_sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid runtime pin.' }
$runtime = Join-Path $root 'runtime'
$destination = Join-Path $runtime 'psf-guard-director-runtime.exe'
if ((Test-Path -LiteralPath $destination) -and
    (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ieq $pin.executable_sha256) {
    Write-Host 'Pinned Director runtime already verified.'
    return
}

$runJson = & gh api "repos/$($pin.repository)/actions/runs/$($pin.run_id)"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the pinned runtime build. Use an already authenticated gh installation.' }
$run = $runJson | ConvertFrom-Json
if ($run.head_sha -cne $pin.commit -or $run.conclusion -cne 'success') { throw 'Pinned runtime build is not a successful matching commit.' }
$artifactJson = & gh api "repos/$($pin.repository)/actions/artifacts/$($pin.artifact_id)"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the pinned runtime artifact.' }
$artifact = $artifactJson | ConvertFrom-Json
if ($artifact.expired -or $artifact.name -cne $pin.artifact_name -or
    $artifact.workflow_run.id -ne $pin.run_id -or $artifact.workflow_run.head_sha -cne $pin.commit) {
    throw 'Runtime artifact identity mismatch or expired artifact. A reviewed pin update is required.'
}

$staging = Join-Path $root "artifacts/fetch-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
& gh run download $pin.run_id --repo $pin.repository --name $pin.artifact_name --dir $staging
if ($LASTEXITCODE -ne 0) { throw 'Runtime download failed.' }
$files = @(Get-ChildItem -LiteralPath $staging -Recurse -File)
if ($files.Count -ne 1 -or $files[0].Name -cne 'psf-guard-director-runtime.exe') { throw 'Unexpected runtime artifact contents.' }
if ((Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash -ine $pin.executable_sha256) {
    throw 'Runtime checksum mismatch. Nothing was installed.'
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-Item -LiteralPath $files[0].FullName -Destination $destination -Force
Write-Host "Verified Director runtime $($pin.runtime_version), engine $($pin.engine_version)."
