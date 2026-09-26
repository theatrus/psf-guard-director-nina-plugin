#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NinaDirectory,
    [Parameter(Mandatory)][string]$PluginZip,
    [switch]$AscomSequence
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$nina = Join-Path (Resolve-Path -LiteralPath $NinaDirectory) 'NINA.exe'
if ((Get-Item -LiteralPath $nina).VersionInfo.FileVersion -ne '3.3.0.1058') {
    throw 'This smoke test requires NINA 3.3 nightly #58 (3.3.0.1058).'
}
$zip = (Resolve-Path -LiteralPath $PluginZip).Path
$hookOutput = Join-Path $repo 'artifacts/nina-hook-build'
dotnet build (Join-Path $PSScriptRoot 'NinaIsolation/NinaIsolation.csproj') --configuration Release -p:RestoreLockedMode=true --output $hookOutput
if ($LASTEXITCODE -ne 0) { throw 'Isolation hook build failed.' }
$builtHook = Join-Path $hookOutput 'NinaIsolation.dll'
$root = Join-Path $repo "artifacts/nina-smoke-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$hook = Join-Path $root 'NinaIsolation.dll'
Copy-Item -LiteralPath $builtHook -Destination $hook
$token = [Guid]::NewGuid().ToString('N')
Set-Content -LiteralPath (Join-Path $root '.director-test-root') -Value $token -NoNewline
# Creating this version directory also prevents NINA's previous-plugin migration.
$plugins = Join-Path $root 'Plugins/3.0.0/PSF Guard Director'
Expand-Archive -LiteralPath $zip -DestinationPath $plugins
$arguments = @{}
if ($AscomSequence) {
    $probeProject = Join-Path $PSScriptRoot 'SimulatorProbe/SimulatorProbe.csproj'
    dotnet build $probeProject --configuration Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Simulator probe build failed.' }
    $probeOutput = Join-Path $PSScriptRoot 'SimulatorProbe/bin/Release/net10.0-windows7.0'
    foreach ($name in @('PSF Guard Director.dll', 'PsfGuard.Director.Runtime.dll')) {
        if ((Get-FileHash (Join-Path $plugins $name)).Hash -ne (Get-FileHash (Join-Path $probeOutput $name)).Hash) {
            throw 'Build a fresh plugin ZIP from this checkout before running its simulator probe.'
        }
    }
    Copy-Item -LiteralPath (Join-Path $probeOutput 'PsfGuard.Director.SimulatorProbe.dll') -Destination $plugins
    $profileId = [Guid]::NewGuid().ToString('D')
    $profiles = Join-Path $root 'Profiles'
    New-Item -ItemType Directory -Path $profiles, (Join-Path $root 'images') | Out-Null
    [xml]$profile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SimulatorProbe/ascom.profile.xml') -Raw
    $profile.Profile.Id = $profileId
    $profile.Profile.ImageFileSettings.FilePath = Join-Path $root 'images'
    $profile.Profile.SequenceSettings.DefaultSequenceFolder = $root
    $profile.Profile.SequenceSettings.SequencerTargetsFolder = Join-Path $root 'Targets'
    $profile.Profile.SequenceSettings.SequencerTemplatesFolder = Join-Path $root 'Templates'
    $profile.Save((Join-Path $profiles "$profileId.profile"))
    $sequence = Join-Path $root 'smoke.sequence.json'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SimulatorProbe/smoke.sequence.json') -Destination $sequence
    $arguments.ArgumentList = @('-p', $profileId, '-s', "`"$sequence`"", '-r')
}
$process = Start-Process -FilePath $nina -WorkingDirectory $NinaDirectory -WindowStyle Hidden -PassThru @arguments -Environment @{
    DOTNET_STARTUP_HOOKS = $hook
    DIRECTOR_NINA_TEST_ROOT = $root
    DIRECTOR_NINA_TEST_TOKEN = $token
} -RedirectStandardError (Join-Path $root 'stderr.txt') -RedirectStandardOutput (Join-Path $root 'stdout.txt')
$ready = Join-Path $root 'isolation-ready.txt'
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while (!(Test-Path -LiteralPath $ready) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 100
}
if (!(Test-Path -LiteralPath $ready) -or (Get-Content -LiteralPath $ready -Raw) -ne $root) {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    throw "NINA did not confirm profile isolation. Inspect $root/stderr.txt."
}
[pscustomobject]@{ ProcessId = $process.Id; TestRoot = $root; Executable = $nina }
