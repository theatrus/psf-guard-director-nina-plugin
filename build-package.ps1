[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/fetch-runtime.ps1"
    & dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & dotnet build --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $output = Join-Path $PSScriptRoot 'src/PsfGuard.Director.Plugin/bin/Release/net10.0-windows7.0'
    $stage = Join-Path $PSScriptRoot "artifacts/package-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path (Join-Path $stage 'runtime') -Force | Out-Null
    foreach ($name in @('PSF Guard Director.dll', 'PsfGuard.Director.Runtime.dll')) {
        Copy-Item -LiteralPath (Join-Path $output $name) -Destination $stage
    }
    Copy-Item -LiteralPath "$PSScriptRoot/runtime/psf-guard-director-runtime.exe" -Destination (Join-Path $stage 'runtime')
    Copy-Item -LiteralPath "$PSScriptRoot/runtime.lock.json", "$PSScriptRoot/LICENSE" -Destination $stage
    $archive = Join-Path $PSScriptRoot 'artifacts/PSFGuardDirector-0.1.0.0-dev.zip'
    Compress-Archive -Path "$stage/*" -DestinationPath $archive -Force
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $actual = @($zip.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
        $expected = @('PSF Guard Director.dll', 'PsfGuard.Director.Runtime.dll', 'runtime/psf-guard-director-runtime.exe', 'runtime.lock.json', 'LICENSE') | Sort-Object
        if (Compare-Object $actual $expected) { throw 'Unexpected files in plugin package.' }
    } finally { $zip.Dispose() }
    Write-Host "Development package: $archive"
} finally { Pop-Location }
