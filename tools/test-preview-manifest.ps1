#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath "$PSScriptRoot/../packaging/manifest.template.json" -Raw
& "$PSScriptRoot/validate-preview-manifest.ps1" -Manifest ($template | ConvertFrom-Json -AsHashtable)
foreach ($invalid in @('channel', 'tag', 'label', 'missing-description', 'missing-icon')) {
    $manifest = $template | ConvertFrom-Json -AsHashtable
    switch ($invalid) {
        channel { $manifest.Channel = 'Beta' }
        tag { $manifest.Tags = @('director') }
        label { $manifest.Descriptions.ShortDescription = 'Production acquisition' }
        missing-description { $manifest.Remove('Descriptions') }
        missing-icon { $manifest.Descriptions.Remove('FeaturedImageURL') }
    }
    $rejected = $false
    try { & "$PSScriptRoot/validate-preview-manifest.ps1" -Manifest $manifest }
    catch { $rejected = $true }
    if (!$rejected) { throw "Preview guard accepted $invalid manifest." }
}
Write-Host 'Beta manifest guard: current template accepted; five invalid variants rejected.'
