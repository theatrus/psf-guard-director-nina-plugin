[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $root 'runtime.lock.json') -Raw | ConvertFrom-Json
if ($pin.repository -cne 'theatrus/psf-guard' -or $pin.commit -cnotmatch '^[a-f0-9]{40}$' -or
    $pin.executable_sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid runtime pin.' }
$runtime = Join-Path $root 'runtime'
$expected = @{ 'psf-guard-director-runtime.exe' = $pin.executable_sha256 }
$noticeNames = @('SOFARS-LICENSE.txt', 'THIRD_PARTY_NOTICES.md')
if (!$pin.notices_sha256 -or (Compare-Object @($pin.notices_sha256.PSObject.Properties.Name | Sort-Object) $noticeNames -CaseSensitive)) {
    throw 'Runtime pin must include exactly the required license notices.'
}
foreach ($name in $noticeNames) {
    $hash = $pin.notices_sha256.$name
    if ($hash -cnotmatch '^[a-f0-9]{64}$') { throw "Invalid notice pin: $name." }
    $expected[$name] = $hash
}
$verified = $true
foreach ($entry in $expected.GetEnumerator()) {
    $destination = Join-Path $runtime $entry.Key
    if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $entry.Value) { $verified = $false }
}
if ($verified) {
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
if ($files.Count -ne $expected.Count -or
    (Compare-Object @($files | ForEach-Object { [IO.Path]::GetRelativePath($staging, $_.FullName) } | Sort-Object) @($expected.Keys | Sort-Object) -CaseSensitive)) {
    throw 'Unexpected runtime artifact contents.'
}
foreach ($file in $files) {
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $expected[$file.Name]) {
        throw "Runtime checksum mismatch for $($file.Name). Nothing was installed."
    }
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $runtime -Force }
Write-Host "Verified Director runtime $($pin.runtime_version), engine $($pin.engine_version)."
