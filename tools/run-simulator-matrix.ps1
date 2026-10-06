#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PsfGuardExe,
    [Parameter(Mandatory)][string]$NinaDirectory,
    [Parameter(Mandatory)][string]$PluginZip,
    [ValidateSet('offline-native', 'unsafe-park', 'unsafe-stop', 'enclosure', 'horizon-change', 'site-change', 'meridian-change', 'moon-wait', 'priority-refresh', 'deferred-checkin', 'center-failure', 'focus-failure', 'flip-failure')]
    [string[]]$Cases,
    [string]$ArtifactDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)
$ErrorActionPreference = 'Stop'
$scenarios = [ordered]@{
    'offline-native' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; AutomaticWorkloads=$true; NativeImaging=$true; OfflineWorkloadRelease=$true }
    'unsafe-park' = @{ PublicAcquisition=$true; PublicUnsafe=$true }
    'unsafe-stop' = @{ PublicAcquisition=$true; PublicUnsafe=$true; AbortWithoutPark=$true }
    'enclosure' = @{ PublicAcquisition=$true; EnclosureClosure=$true }
    'horizon-change' = @{ PublicAcquisition=$true; ConstraintChange='horizon' }
    'site-change' = @{ PublicAcquisition=$true; ConstraintChange='site' }
    'meridian-change' = @{ PublicAcquisition=$true; ConstraintChange='meridian' }
    'moon-wait' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; MoonAvoidance=$true }
    'priority-refresh' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; AutomaticWorkloads=$true; ProjectOrder=$true; PriorityRefresh=$true }
    'deferred-checkin' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; DeferredCheckIn=$true }
    'center-failure' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; AutomaticWorkloads=$true; NativeImaging=$true; NativeImagingFailure='center' }
    'focus-failure' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; AutomaticWorkloads=$true; NativeImaging=$true; NativeImagingFailure='autofocus' }
    'flip-failure' = @{ PublicAcquisition=$true; LocalTargetScheduling=$true; AutomaticWorkloads=$true; NativeImaging=$true; ForceNativeFlip=$true; NativeImagingFailure='meridian' }
}
$report = Join-Path $ArtifactDirectory "matrix-$([Guid]::NewGuid().ToString('N')).json"
$results = @()
foreach ($case in $scenarios.GetEnumerator()) {
    if ($Cases -and $case.Key -notin $Cases) { continue }
    Write-Host "Simulator case: $($case.Key)"
    $options = $case.Value
    try {
        # Serial execution is intentional: every case shares the ASCOM simulators.
        $result = & "$PSScriptRoot/run-server-plan-smoke.ps1" -PsfGuardExe $PsfGuardExe -NinaDirectory $NinaDirectory -PluginZip $PluginZip -ArtifactDirectory $ArtifactDirectory @options
        $results += [pscustomobject]@{ Case=$case.Key; Passed=$true; Result=$result }
    } catch {
        $results += [pscustomobject]@{ Case=$case.Key; Passed=$false; Error=$_.ToString() }
        throw
    } finally {
        $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report
        Write-Host "Matrix evidence: $report"
    }
}
$results
