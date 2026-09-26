#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../fetch-runtime.ps1'
$root = Join-Path $PSScriptRoot "../artifacts/fetch-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $root
$script:payload = @{
    'psf-guard-director-runtime.exe' = [Text.Encoding]::UTF8.GetBytes('test executable, not runnable')
    'SOFARS-LICENSE.txt' = [Text.Encoding]::UTF8.GetBytes('test license')
    'THIRD_PARTY_NOTICES.md' = [Text.Encoding]::UTF8.GetBytes('test notices')
}
function Hash([byte[]] $bytes) { [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes)) }
$script:pin = @{
    repository = 'theatrus/psf-guard'; commit = ('a' * 40); run_id = 123; artifact_id = 456
    artifact_name = 'director-runtime-windows-x64'; executable_sha256 = (Hash $payload['psf-guard-director-runtime.exe'])
    notices_sha256 = @{
        'SOFARS-LICENSE.txt' = (Hash $payload['SOFARS-LICENSE.txt'])
        'THIRD_PARTY_NOTICES.md' = (Hash $payload['THIRD_PARTY_NOTICES.md'])
    }
    runtime_version = 'test'; engine_version = 'test'
}
$pin | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$root/runtime.lock.json"
$fixtureState = @{ Downloads = 0; Fault = '' }
# The script under test resolves this local fake instead of contacting GitHub.
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'api') {
        if ($args[1] -like '*/actions/runs/*') {
            return (@{ head_sha = $pin.commit; conclusion = 'success' } | ConvertTo-Json)
        }
        return (@{ expired = $false; name = $pin.artifact_name; workflow_run = @{ id = $pin.run_id; head_sha = $pin.commit } } | ConvertTo-Json)
    }
    if ($args[0] -ne 'run' -or $args[1] -ne 'download') { throw 'Unexpected gh call.' }
    $fixtureState.Downloads++
    $destination = $args[[Array]::IndexOf($args, '--dir') + 1]
    foreach ($entry in $payload.GetEnumerator()) {
        if ($fixtureState.Fault -eq 'missing' -and $entry.Key -eq 'SOFARS-LICENSE.txt') { continue }
        $bytes = if ($fixtureState.Fault -eq 'corrupt' -and $entry.Key -eq 'SOFARS-LICENSE.txt') { [byte[]]@(1, 2, 3) } else { $entry.Value }
        [IO.File]::WriteAllBytes((Join-Path $destination $entry.Key), $bytes)
    }
    if ($fixtureState.Fault -eq 'extra') { [IO.File]::WriteAllText((Join-Path $destination 'unexpected.txt'), 'unexpected') }
}
function Assert([bool] $condition, [string] $message) { if (!$condition) { throw $message } }
& "$root/fetch-runtime.ps1"
Assert ($fixtureState.Downloads -eq 1) 'First fetch must download.'
& "$root/fetch-runtime.ps1"
Assert ($fixtureState.Downloads -eq 1) 'Verified cache must not download.'
foreach ($name in @('SOFARS-LICENSE.txt', 'THIRD_PARTY_NOTICES.md')) {
    [IO.File]::WriteAllText("$root/runtime/$name", 'corrupt cache')
    & "$root/fetch-runtime.ps1"
    Assert ((Get-FileHash -LiteralPath "$root/runtime/$name").Hash -ieq $pin.notices_sha256[$name]) 'Notice cache was not repaired.'
}
Remove-Item -LiteralPath "$root/runtime/SOFARS-LICENSE.txt"
& "$root/fetch-runtime.ps1"
Assert (Test-Path -LiteralPath "$root/runtime/SOFARS-LICENSE.txt") 'Missing notice was not restored.'
foreach ($case in @('missing', 'corrupt', 'extra')) {
    $fixtureState.Fault = $case
    [IO.File]::WriteAllText("$root/runtime/psf-guard-director-runtime.exe", 'existing sentinel')
    $rejected = $false
    try { & "$root/fetch-runtime.ps1" } catch { $rejected = $true }
    Assert $rejected "Invalid artifact must fail: $case."
    Assert ([IO.File]::ReadAllText("$root/runtime/psf-guard-director-runtime.exe") -eq 'existing sentinel') 'Invalid artifact changed installed files.'
}
Write-Host 'Runtime fetch regression checks passed.'
