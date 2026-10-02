#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PsfGuardExe,
    [Parameter(Mandatory)][string]$NinaDirectory,
    [Parameter(Mandatory)][string]$PluginZip,
    [string]$Python = 'python',
    [switch]$PublicAcquisition,
    [switch]$PublicUnsafe,
    [switch]$AbortWithoutPark,
    [switch]$EnclosureClosure,
    [switch]$AutomaticWorkloads,
    [switch]$LocalTargetScheduling,
    [switch]$MoonAvoidance,
    [string]$ArtifactDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')
)
$ErrorActionPreference = 'Stop'
if ($EnclosureClosure -and (!$PublicAcquisition -or $PublicUnsafe -or $AutomaticWorkloads -or $LocalTargetScheduling -or $MoonAvoidance)) { throw 'EnclosureClosure requires only PublicAcquisition.' }
if ($MoonAvoidance -and (!$PublicAcquisition -or !$LocalTargetScheduling -or $AutomaticWorkloads -or $PublicUnsafe)) { throw 'MoonAvoidance requires safe public local scheduling without automatic workloads.' }
if ($PublicUnsafe -and !$PublicAcquisition) { throw 'PublicUnsafe requires PublicAcquisition.' }
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
    $fixture = "$root/fixture.json"
    @{ Endpoint=$endpoint; CoordinatorInstanceId=$status.data.instance_id; CatalogId=$catalog;
        RigId=$applied.data.binding.rig.id; ActivateSimulatorPlan=$true; ExerciseOutage=(!$AutomaticWorkloads); PublicAcquisition=[bool]$PublicAcquisition; PublicUnsafe=[bool]$PublicUnsafe; AbortWithoutPark=[bool]$AbortWithoutPark; EnclosureClosure=[bool]$EnclosureClosure; AutomaticWorkloads=[bool]$AutomaticWorkloads; LocalTargetScheduling=[bool]$LocalTargetScheduling; MoonAvoidance=[bool]$MoonAvoidance } |
        ConvertTo-Json | Set-Content -LiteralPath $fixture
    $started = & "$PSScriptRoot/start-nina-smoke.ps1" -NinaDirectory $NinaDirectory -PluginZip $PluginZip -AscomSequence -CoordinatorFixture $fixture -ArtifactDirectory $ArtifactDirectory
    $started = $started | Where-Object { $_.PSObject.Properties.Name -contains 'ProcessId' } | Select-Object -Last 1
    if (!$started) { throw 'NINA launcher did not return an isolated process.' }
    $nina = Get-Process -Id $started.ProcessId
    $deadline = [DateTime]::UtcNow.AddMinutes(6)
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
    if (!$evidence.passed -or (!$AutomaticWorkloads -and (!$stopped -or !$resumed)) -or !$evidence.program_revision -or !$evidence.live_status_verified) {
        throw "Server-plan smoke failed; inspect $($result.FullName)"
    }
    if ($PublicAcquisition -and !$evidence.equipment_review_verified) { throw 'Public acquisition did not verify staged equipment review.' }
    if ($AutomaticWorkloads -and !$evidence.automatic_workload_verified) { throw 'Automatic session did not verify terminal release and bounded pending-assessment wait.' }
    if ($LocalTargetScheduling -and !$evidence.local_targets_verified) { throw 'Local multi-target priority and native hooks were not verified.' }
    if ($MoonAvoidance -and !$evidence.moon_avoidance_verified) { throw 'Moon-blocked high-priority work and parked wait were not verified.' }
    [pscustomobject]@{ Passed=$true; Evidence=$result.FullName; ServerArtifacts=$root; Nina=$evidence.nina; ProgramRevision=$evidence.program_revision }
}
finally {
    if ($nina -and !$nina.HasExited) {
        [void]$nina.CloseMainWindow()
        if (!$nina.WaitForExit(15000)) { Write-Warning "Isolated NINA still open: PID $($nina.Id). Close it after inspecting its test sequence." }
    }
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
}
