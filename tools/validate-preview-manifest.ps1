#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][hashtable]$Manifest)
$ErrorActionPreference = 'Stop'
if ($Manifest.ContainsKey('Channel') -or $Manifest.Tags -notcontains 'experimental' -or
    $Manifest.Descriptions.ShortDescription -cnotmatch '^Experimental\s') {
    throw 'The shared registry feed requires explicit preview labeling and no Channel override.'
}
