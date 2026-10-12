#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch] $Remove)
$ErrorActionPreference = 'Stop'
$engine = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime\app\_internal\bin\uxplay.exe'))
$ruleNames = @('IPhoneMirror-Prototype-TCP', 'IPhoneMirror-Prototype-UDP')
if ($Remove) {
    foreach ($ruleName in $ruleNames) {
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    Write-Output 'Removed only the two iPhone Mirror prototype rules.'
    return
}
if (-not (Test-Path -LiteralPath $engine)) { throw 'Set up the runtime first.' }
if (-not (Get-NetConnectionProfile | Where-Object NetworkCategory -EQ 'Private')) {
    throw 'No Private network is active. On a trusted home network, set its Windows network profile to Private first. No settings were changed.'
}
foreach ($protocol in @('TCP', 'UDP')) {
    $ruleName = 'IPhoneMirror-Prototype-' + $protocol
    $existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        $filter = $existing | Get-NetFirewallApplicationFilter
        if ($filter.Program -ne $engine) { throw "Existing rule points to another installation: $ruleName" }
        Write-Output "Already exists: $ruleName"
        continue
    }
    New-NetFirewallRule -Name $ruleName -DisplayName ('iPhone Mirror prototype ' + $protocol) -Direction Inbound -Action Allow -Program $engine -Protocol $protocol -LocalPort '7100-7102' -RemoteAddress LocalSubnet -Profile Private | Out-Null
}
Write-Output 'Allowed this receiver on TCP/UDP 7100-7102, Private profile, LocalSubnet only. Bonjour mDNS uses its existing service rule.'
