[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# NINA nightly 1058 ships these exact bytes. Tests only; never package them.
$baseUri = 'https://media.githubusercontent.com/media/isbeorn/nina.external/de19c6cf64128e93ed78944ddc057ba65fb12c49'
$files = @{
    'x64/SOFA/SOFA_2023_10_11.dll' = '6F84A31FF7A9C74C50825ABEED4209462D1EECBB32A76903E33F834B4327B4DA'
    'x64/NOVAS/NOVAS31lib.dll' = '829859DB52CE15A85454E7B76D3FC387529386ED115E17AA4ACE228783DEB510'
    'JPLEPH' = 'E7AE604FB0BB2C31BEB2D1DC919459D5CC688DDCCBBE20A7E528FF6D3F1BD257'
}
$directory = Join-Path $PSScriptRoot '../artifacts/nina-test'
foreach ($file in $files.GetEnumerator()) {
    $destination = Join-Path $directory $file.Key
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $file.Value) {
        continue
    }
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $uri = if ($file.Key -eq 'JPLEPH') {
            'https://raw.githubusercontent.com/isbeorn/nina.external/de19c6cf64128e93ed78944ddc057ba65fb12c49/JPLEPH'
        } else { "$baseUri/$($file.Key)" }
        Invoke-WebRequest -Uri $uri -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $file.Value) {
            throw "NINA test dependency checksum mismatch: $($file.Key)."
        }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
Write-Host 'Verified pinned NINA astronomy test dependencies.'
