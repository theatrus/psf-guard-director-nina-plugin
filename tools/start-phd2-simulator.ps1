#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$instance = Get-Random -Minimum 2000 -Maximum 3000
$key = "HKCU:\Software\StarkLabs\PHDGuidingV2-instance$instance"
$port = 4400 + $instance - 1
if (Test-Path -LiteralPath $key) { throw 'Refusing to reuse an existing PHD2 instance.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
try { $listener.Start() } finally { $listener.Stop() }
$root = New-Item -ItemType Directory -Path (Join-Path $ArtifactDirectory "phd2-$([Guid]::NewGuid().ToString('N'))")
$logPath = $root.FullName.Replace('\', '/')
$config = Join-Path $root.FullName 'simulator.txt'
$entries = @(
    '/ConfigVersion|2|2001', '/currentProfile|2|1', '/ServerMode|1|1', '/Update/enabled|1|0',
    "/frame/logdir|1|$logPath", '/profile/1/name|1|Director isolated simulator',
    '/profile/1/camera/LastMenuChoice|1|Simulator', '/profile/1/scope/LastMenuChoice|1|On-camera',
    '/profile/1/camera/AutoLoadDarks|1|0', '/profile/1/camera/AutoLoadDefectMap|1|0',
    '/profile/1/PixelSize|1|3.75', '/profile/1/focalLength|2|400',
    '/profile/1/ExposureDuration|2|500', '/profile/1/SimCam/use_pe|1|0',
    '/profile/1/SimCam/dec_drift|1|0', '/profile/1/SimCam/dec_backlash|1|0'
)
[IO.File]::WriteAllLines($config, @('PHD Config 1') + @($entries | ForEach-Object { $_.Replace('|', "`t") }))
$process = $null
try {
    $loader = Start-Process -FilePath $exe -ArgumentList @('-i', $instance, '-l', "`"$config`"") -WindowStyle Hidden -PassThru
    if (!$loader.WaitForExit(15000)) { $loader.Kill(); $loader.WaitForExit(); throw 'PHD2 simulator configuration timed out.' }
    if ($loader.ExitCode -ne 0) { throw 'PHD2 simulator configuration failed.' }
    $process = Start-Process -FilePath $exe -ArgumentList @('-i', $instance) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ($true) {
        $client = [Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $port); break }
        catch { $client.Dispose(); if ($process.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw }; Start-Sleep -Milliseconds 200 }
    }
    try {
        $stream = $client.GetStream()
        $stream.ReadTimeout = 10000
        $writer = [IO.StreamWriter]::new($stream); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($stream)
        $writer.WriteLine('{"method":"get_current_equipment","id":1}')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $line = $reader.ReadLine()
            if ($null -eq $line -or [DateTime]::UtcNow -ge $deadline) { throw 'PHD2 equipment validation timed out or disconnected.' }
            $reply = $line | ConvertFrom-Json
        } while ($reply.id -ne 1)
        if ($reply.error -or $reply.result.camera.name -ne 'Simulator' -or $reply.result.mount.name -ne 'On Camera') {
            throw 'PHD2 did not confirm simulator-only equipment.'
        }
    } finally { $client.Dispose() }
    [pscustomobject]@{ Process=$process; Instance=$instance; Port=$port; Executable=$exe; RegistryKey=$key; Artifacts=$root.FullName }
} catch {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ((Test-Path -LiteralPath $key) -and
        (Get-Item -LiteralPath $key).Name -eq "HKEY_CURRENT_USER\Software\StarkLabs\PHDGuidingV2-instance$instance" -and
        (Get-ItemProperty -LiteralPath "$key\profile\1" -Name name -ErrorAction SilentlyContinue).name -eq 'Director isolated simulator') {
        Remove-Item -LiteralPath $key -Recurse
    }
    throw
}
