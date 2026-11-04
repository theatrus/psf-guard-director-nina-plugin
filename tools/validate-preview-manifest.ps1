#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][hashtable]$Manifest)
$ErrorActionPreference = 'Stop'
if ($Manifest.ContainsKey('Channel') -or $Manifest.Tags -notcontains 'beta' -or
    $Manifest.Descriptions.ShortDescription -cnotmatch '^Beta\s' -or
    $Manifest.Descriptions.FeaturedImageURL -cne 'https://raw.githubusercontent.com/theatrus/psf-guard-director-nina-plugin/main/assets/psf-guard-director.png') {
    throw 'The shared registry feed requires explicit beta labeling, the Director icon and no Channel override.'
}
