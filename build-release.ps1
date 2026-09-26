#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/build-package.ps1"
    $manifest = Get-Content -LiteralPath "$PSScriptRoot/packaging/manifest.template.json" -Raw | ConvertFrom-Json -AsHashtable
    $parts = @('Major', 'Minor', 'Patch', 'Build' | ForEach-Object { $manifest.Version[$_] })
    $version = [version]::new($parts[0], $parts[1], $parts[2], $parts[3])
    $assembly = Join-Path $PSScriptRoot 'src/PsfGuard.Director.Plugin/bin/Release/net10.0-windows7.0/PSF Guard Director.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($assembly).Version -ne $version) {
        throw 'Manifest version does not match the built plugin.'
    }
    if ($manifest.ContainsKey('Channel') -or $manifest.Tags -notcontains 'experimental' -or
        !$manifest.Descriptions.ShortDescription.Contains('Acquisition is not yet available.')) {
        throw 'The shared registry feed requires explicit preview labeling and no Channel override.'
    }
    $tag = "$version-preview.1"
    $name = "PSFGuardDirector-$version.zip"
    $archive = Join-Path $PSScriptRoot "artifacts/$name"
    Copy-Item -LiteralPath "$PSScriptRoot/artifacts/PSFGuardDirector-0.1.0.0-dev.zip" -Destination $archive
    $manifest.Installer = @{
        URL = "https://github.com/theatrus/psf-guard-director-nina-plugin/releases/download/$tag/$name"
        Type = 'ARCHIVE'
        Checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        ChecksumType = 'SHA256'
    }
    $output = Join-Path $PSScriptRoot "artifacts/PSFGuardDirector-$version.manifest.json"
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $output -Encoding utf8NoBOM
    [pscustomobject]@{ Tag = $tag; Archive = $archive; Manifest = $output; Checksum = $manifest.Installer.Checksum }
} finally { Pop-Location }
