[CmdletBinding()]
param([switch] $Remove)
$ErrorActionPreference = 'Stop'
$engine = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\app\_internal\bin\uxplay.exe'))
. (Join-Path $PSScriptRoot 'scripts\FirewallRules.ps1')
if ($Remove) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Removing legacy rules requires an administrator PowerShell window.' }
    Remove-MirrorRules @('IPhoneMirror-Prototype-TCP','IPhoneMirror-Prototype-UDP') $engine 'PersistentStore'
    Write-Output 'Removed only the matching legacy prototype rules.'
} else {
    Write-Output 'Firewall access is managed by the app. This script no longer creates persistent allow rules.'
    Get-MirrorRules ((Get-MirrorPrefix $engine) + '-*') 'ActiveStore' | Select-Object Name,Enabled,Profile,PolicyStoreSourceType
    foreach ($name in @('IPhoneMirror-Prototype-TCP','IPhoneMirror-Prototype-UDP')) {
        Get-MirrorRules $name 'PersistentStore' | Select-Object Name,Enabled,Profile,PolicyStoreSourceType
    }
}
