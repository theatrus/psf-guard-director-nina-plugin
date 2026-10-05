#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PsfGuardExe,
    [Parameter(Mandatory)][string]$NinaDirectory,
    [Parameter(Mandatory)][string]$PluginZip,
    [string]$Python = 'python',
    [switch]$PublicAcquisition,
    [switch]$PublicUnsafe,
    [switch]$DeferredCheckIn,
    [switch]$OfflineWorkloadRelease,
    [switch]$AbortWithoutPark,
    [switch]$EnclosureClosure,
    [switch]$AutomaticWorkloads,
    [switch]$LocalTargetScheduling,
    [switch]$MoonAvoidance,
    [switch]$ObservingPreferences,
    [switch]$ProjectOrder,
    [switch]$PriorityRefresh,
    [switch]$NativeImaging,
    [ValidateSet('focus-once', 'focus-always')][string]$RecoveryScenario,
    [ValidateSet('workload-wait', 'target-wait')][string]$NightEndScenario,
    [ValidateSet('safety-wait', 'roof-wait', 'roof-night-end', 'safety-exposure', 'roof-exposure')][string]$WeatherHoldScenario,
    [switch]$ForceNativeFlip,
    [string]$Phd2Executable,
    [ValidateSet('center', 'autofocus', 'meridian')][string]$NativeImagingFailure,
    [ValidateSet('horizon', 'site', 'meridian')][string]$ConstraintChange,
    [string]$ArtifactDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)
$ErrorActionPreference = 'Stop'
if ($NightEndScenario -and (!$PublicAcquisition -or !$LocalTargetScheduling -or $NativeImaging -or $PublicUnsafe -or $RecoveryScenario -or $ConstraintChange -or $EnclosureClosure -or $PriorityRefresh -or $DeferredCheckIn -or $OfflineWorkloadRelease)) { throw 'NightEndScenario requires safe public local scheduling without other fault scenarios.' }
if ($NightEndScenario -eq 'workload-wait' -and (!$AutomaticWorkloads -or $MoonAvoidance)) { throw 'Workload night end requires automatic workloads without Moon avoidance.' }
if ($NightEndScenario -eq 'target-wait' -and (!$MoonAvoidance -or $AutomaticWorkloads)) { throw 'Target-wait night end requires Moon avoidance without automatic workloads.' }
if ($WeatherHoldScenario -and $WeatherHoldScenario -notlike '*-exposure' -and $NightEndScenario -ne 'target-wait') { throw 'Weather holds require the target-wait night-end fixture.' }
if ($WeatherHoldScenario -eq 'safety-exposure' -and !$PublicUnsafe) { throw 'Safety exposure interruption requires PublicUnsafe.' }
if ($WeatherHoldScenario -eq 'roof-exposure' -and !$EnclosureClosure) { throw 'Roof exposure interruption requires EnclosureClosure.' }
if ($NativeImaging -and (!$PublicAcquisition -or !$LocalTargetScheduling -or $ConstraintChange -or $PublicUnsafe -or $EnclosureClosure -or $PriorityRefresh)) { throw 'NativeImaging requires safe public local scheduling.' }
if ($NativeImagingFailure -and (!$NativeImaging -or !$AutomaticWorkloads)) { throw 'NativeImagingFailure requires native automatic workloads.' }
if ($RecoveryScenario -and (!$NativeImaging -or !$AutomaticWorkloads)) { throw 'RecoveryScenario requires native automatic workloads.' }
if ($RecoveryScenario -eq 'focus-always') { $NativeImagingFailure = 'autofocus' }
if ($ForceNativeFlip -and (!$NativeImaging -or !$AutomaticWorkloads -or ($NativeImagingFailure -and $NativeImagingFailure -ne 'meridian'))) { throw 'ForceNativeFlip requires native automatic workloads without other faults.' }
if ($NativeImagingFailure -eq 'meridian' -and !$ForceNativeFlip) { throw 'Meridian failure requires ForceNativeFlip.' }
if ($Phd2Executable -and (!$ForceNativeFlip -or $NativeImagingFailure)) { throw 'PHD2 simulator requires the successful native flip scenario.' }
if ($ConstraintChange -and (!$PublicAcquisition -or $PublicUnsafe -or $AutomaticWorkloads -or $LocalTargetScheduling -or $DeferredCheckIn -or $EnclosureClosure)) { throw 'ConstraintChange requires only PublicAcquisition.' }
if ($ProjectOrder -and (!$LocalTargetScheduling -or !$PublicAcquisition -or $ObservingPreferences -or $MoonAvoidance -or $PublicUnsafe)) { throw 'ProjectOrder requires safe public local scheduling without weights or Moon-only waits.' }
if ($PriorityRefresh -and (!$ProjectOrder -or !$AutomaticWorkloads -or $OfflineWorkloadRelease -or $DeferredCheckIn)) { throw 'PriorityRefresh requires live automatic ranked workloads.' }
if ($ObservingPreferences -and (!$LocalTargetScheduling -or !$PublicAcquisition -or $MoonAvoidance -or $PublicUnsafe -or $EnclosureClosure)) { throw 'ObservingPreferences requires safe public local target scheduling.' }
if ($EnclosureClosure -and (!$PublicAcquisition -or $PublicUnsafe -or $AutomaticWorkloads -or $LocalTargetScheduling -or $MoonAvoidance)) { throw 'EnclosureClosure requires only PublicAcquisition.' }
if ($MoonAvoidance -and (!$PublicAcquisition -or !$LocalTargetScheduling -or $AutomaticWorkloads -or $PublicUnsafe)) { throw 'MoonAvoidance requires safe public local scheduling without automatic workloads.' }
if ($PublicUnsafe -and !$PublicAcquisition) { throw 'PublicUnsafe requires PublicAcquisition.' }
if ($DeferredCheckIn -and (!$PublicAcquisition -or $PublicUnsafe -or $AutomaticWorkloads -or $EnclosureClosure -or $MoonAvoidance)) { throw 'DeferredCheckIn requires safe public acquisition without automatic workloads.' }
if ($OfflineWorkloadRelease -and (!$AutomaticWorkloads -or !$PublicAcquisition -or $DeferredCheckIn)) { throw 'OfflineWorkloadRelease requires automatic public acquisition.' }
if ($AbortWithoutPark -and !$PublicUnsafe) { throw 'AbortWithoutPark requires PublicUnsafe.' }
if ($LocalTargetScheduling -and (!$PublicAcquisition -or $PublicUnsafe)) { throw 'LocalTargetScheduling requires a safe public acquisition run.' }
if ($AutomaticWorkloads -and (!$PublicAcquisition -or $PublicUnsafe)) { throw 'AutomaticWorkloads requires a safe PublicAcquisition run.' }
$root = Join-Path $env:TEMP "director-server-plan-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root, "$root/images" | Out-Null
$exe = Join-Path $root 'psf-guard-cli.exe'
Copy-Item -LiteralPath (Resolve-Path -LiteralPath $PsfGuardExe).Path -Destination $exe
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$endpoint = "http://127.0.0.1:$port/"
$serverArgs = @('server', '--registry', "$root/registry.json", '--director-meta', "$root/meta.sqlite",
    '--cache-dir', "$root/cache", '--host', '127.0.0.1', '--port', $port, '--allow-database-management')
$script:server = $null
$nina = $null
$phd2 = $null
$generation = 0
function Start-TestServer {
    $script:generation++
    $script:server = Start-Process -FilePath $exe -ArgumentList $serverArgs -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$root/server-$generation.out" -RedirectStandardError "$root/server-$generation.err"
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline -and !$server.HasExited) {
        try {
            $answer = Invoke-RestMethod "${endpoint}api/director/v1/status" -TimeoutSec 2
            $owner = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $port -State Listen -ErrorAction Stop
            if ($owner.OwningProcess -ne $server.Id -or !$answer.data.enabled) { throw 'Listener is not the isolated Director server.' }
            return $answer
        }
        catch { Start-Sleep -Milliseconds 200 }
    }
    throw "Isolated server failed to start; see $root"
}
function Json-Request($method, $path, $body) {
    Invoke-RestMethod "${endpoint}api/$path" -Method $method -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 20) -TimeoutSec 20
}
try {
    $status = Start-TestServer
    $slug = 'director-simulator'
    $db = "$root/rig.sqlite"
    Json-Request Post 'databases/create' @{ name='Director simulator'; slug=$slug; db_path=$db; image_dirs=@("$root/images"); backfill=$false } | Out-Null
    # This is a fresh disposable catalog. Seed only TS's owning profile; all
    # plans, targets and recipes are subsequently created by the server API.
    & $Python -c 'import sqlite3,sys,uuid; c=sqlite3.connect(sys.argv[1]); assert c.execute("SELECT count(*) FROM project").fetchone()[0] == 0; c.execute("INSERT INTO profilepreference (profileId,guid) VALUES (?,?)", (str(uuid.uuid4()),str(uuid.uuid4()))); c.commit(); c.close()' $db
    if ($LASTEXITCODE -ne 0) { throw 'Disposable profile seed failed.' }
    $catalog = [Guid]::NewGuid().ToString('D')
    $preview = Json-Request Post "director/v1/catalogs/$slug/rig/preview" @{catalog_id=$catalog}
    $applied = Json-Request Post "director/v1/catalogs/$slug/rig/apply" @{plan=@{catalog_id=$catalog};preview_digest=$preview.data.preview_digest}
    if ($Phd2Executable) { $phd2 = & "$PSScriptRoot/start-phd2-simulator.ps1" -Executable $Phd2Executable -ArtifactDirectory $root }
    $fixture = "$root/fixture.json"
    @{ Endpoint=$endpoint; CoordinatorInstanceId=$status.data.instance_id; CatalogId=$catalog;
        RigId=$applied.data.binding.rig.id; ActivateSimulatorPlan=$true; ExerciseOutage=((!$AutomaticWorkloads -or $OfflineWorkloadRelease) -and !$DeferredCheckIn); PublicAcquisition=[bool]$PublicAcquisition; PublicUnsafe=[bool]$PublicUnsafe; AbortWithoutPark=[bool]$AbortWithoutPark; EnclosureClosure=[bool]$EnclosureClosure; AutomaticWorkloads=[bool]$AutomaticWorkloads; LocalTargetScheduling=[bool]$LocalTargetScheduling; MoonAvoidance=[bool]$MoonAvoidance; ObservingPreferences=[bool]$ObservingPreferences; DeferredCheckIn=[bool]$DeferredCheckIn; OfflineWorkloadRelease=[bool]$OfflineWorkloadRelease } |
        ForEach-Object { $_.WeatherHoldScenario=$(if ($WeatherHoldScenario) { $WeatherHoldScenario } else { $null }); $_.NightEndScenario=$(if ($NightEndScenario) { $NightEndScenario } else { $null }); $_.RecoveryScenario=$(if ($RecoveryScenario) { $RecoveryScenario } else { $null }); $_.ProjectOrder=[bool]$ProjectOrder; $_.PriorityRefresh=[bool]$PriorityRefresh; $_.ConstraintChange=$(if ($ConstraintChange) { $ConstraintChange } else { $null }); $_.NativeImaging=[bool]$NativeImaging; $_.ForceNativeFlip=[bool]$ForceNativeFlip; $_.NativeImagingFailure=$(if ($NativeImagingFailure) { $NativeImagingFailure } else { $null }); $_ } |
        ForEach-Object { if ($phd2) { $_.Phd2 = @{ Executable=$phd2.Executable; Instance=$phd2.Instance; Port=$phd2.Port } }; $_ } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $fixture
    $started = & "$PSScriptRoot/start-nina-smoke.ps1" -NinaDirectory $NinaDirectory -PluginZip $PluginZip -AscomSequence -CoordinatorFixture $fixture -ArtifactDirectory $ArtifactDirectory
    $started = $started | Where-Object { $_.PSObject.Properties.Name -contains 'ProcessId' } | Select-Object -Last 1
    if (!$started) { throw 'NINA launcher did not return an isolated process.' }
    $nina = Get-Process -Id $started.ProcessId
    $deadline = [DateTime]::UtcNow.AddMinutes($(if ($phd2) { 10 } else { 6 }))
    $stopped = $false
    $resumed = $false
    $result = $null
    while ([DateTime]::UtcNow -lt $deadline -and !$nina.HasExited) {
        if (!$stopped -and (Test-Path "$($started.TestRoot)/stop-server.request")) {
            Stop-Process -Id $server.Id
            $server.WaitForExit()
            Set-Content "$($started.TestRoot)/server-stopped" 'stopped'
            $stopped = $true
        }
        if ($stopped -and !$resumed -and (Test-Path "$($started.TestRoot)/start-server.request")) {
            $restarted = Start-TestServer
            if ($restarted.data.instance_id -ne $status.data.instance_id) { throw 'Coordinator identity changed on restart.' }
            Set-Content "$($started.TestRoot)/server-started" 'started'
            $resumed = $true
        }
        $result = Get-ChildItem "$($started.TestRoot)/probe" -Filter result.json -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($result) { break }
        Start-Sleep -Milliseconds 200
    }
    if (!$result) { throw "No simulator result; inspect $($started.TestRoot)" }
    $evidence = Get-Content -LiteralPath $result.FullName -Raw | ConvertFrom-Json
    if (!$evidence.passed -or ((!$AutomaticWorkloads -or $OfflineWorkloadRelease) -and !$DeferredCheckIn -and (!$stopped -or !$resumed)) -or !$evidence.program_revision -or !$evidence.live_status_verified) {
        throw "Server-plan smoke failed; inspect $($result.FullName)"
    }
    if ($PublicAcquisition -and !$evidence.equipment_review_verified) { throw 'Public acquisition did not verify staged equipment review.' }
    if ($NightEndScenario -and !(Test-Path -LiteralPath (Join-Path $result.DirectoryName 'night-end-verified.txt'))) { throw 'Normal night end and following native sequence step were not verified.' }
    if ($WeatherHoldScenario -and !(Test-Path -LiteralPath (Join-Path $result.DirectoryName 'weather-hold-verified.txt'))) { throw 'Weather hold was not verified.' }
    if ($AutomaticWorkloads -and !$NativeImagingFailure -and !$evidence.automatic_workload_verified) { throw 'Automatic session did not verify terminal release and bounded pending-assessment wait.' }
    if ($LocalTargetScheduling -and !$NativeImagingFailure -and !$evidence.local_targets_verified) { throw 'Local multi-target priority and native hooks were not verified.' }
    if ($MoonAvoidance -and !$evidence.moon_avoidance_verified) { throw 'Moon-blocked high-priority work and parked wait were not verified.' }
    if ($ObservingPreferences -and !$evidence.observing_preferences_verified) { throw 'Weighted observing preferences did not change native target order.' }
    if ($ProjectOrder -and !$evidence.project_order_verified) { throw 'Ranked project execution was not verified.' }
    if ($PriorityRefresh -and !$evidence.priority_refresh_verified) { throw 'Safe-boundary priority handoff was not verified.' }
    if ($ConstraintChange -and !$evidence.constraint_change_verified) { throw 'Constraint-change cancellation and no-restart were not verified.' }
    if ($NativeImagingFailure -and !$evidence.native_failure_verified) { throw 'Native failure did not prevent acquisition.' }
    if ($NativeImaging -and !$NativeImagingFailure -and !$evidence.native_imaging_verified) { throw 'Native imaging flow was not verified.' }
    if ($ForceNativeFlip -and (!$evidence.native_flip -or $evidence.native_flip.attempts -ne 1)) { throw 'Forced native flip was not exercised.' }
    if ($Phd2Executable -and (!$evidence.phd2_simulator -or $evidence.native_flip.recenter_solutions -ne 1 -or
        $null -eq $evidence.rotator_final_position -or [Math]::Abs($evidence.rotator_final_position - 30) -gt 1)) {
        throw 'PHD2 recovery, parsed recenter solution and requested rotation were not verified.'
    }
    [pscustomobject]@{ Passed=$true; Evidence=$result.FullName; ServerArtifacts=$root; Nina=$evidence.nina; ProgramRevision=$evidence.program_revision }
}
finally {
    if ($nina -and !$nina.HasExited) {
        [void]$nina.CloseMainWindow()
        if (!$nina.WaitForExit(15000)) { Write-Warning "Isolated NINA still open: PID $($nina.Id). Close it after inspecting its test sequence." }
    }
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
    if ($phd2 -and !$phd2.Process.HasExited) {
        [void]$phd2.Process.CloseMainWindow()
        if (!$phd2.Process.WaitForExit(10000)) { $phd2.Process.Kill(); $phd2.Process.WaitForExit() }
    }
    if ($phd2 -and (Test-Path -LiteralPath $phd2.RegistryKey)) {
        $expected = "HKEY_CURRENT_USER\Software\StarkLabs\PHDGuidingV2-instance$($phd2.Instance)"
        $owned = (Get-Item -LiteralPath $phd2.RegistryKey).Name -eq $expected -and
            (Get-ItemPropertyValue -LiteralPath "$($phd2.RegistryKey)\profile\1" -Name name) -eq 'Director isolated simulator'
        if ($owned) { Remove-Item -LiteralPath $phd2.RegistryKey -Recurse }
        else { Write-Warning 'PHD2 test registry identity changed; preserving it for inspection.' }
    }
}
