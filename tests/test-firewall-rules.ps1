# Safe, non-elevated tests. Every NetSecurity operation is replaced in memory.
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility -ErrorAction Stop
$PSModuleAutoLoadingPreference = 'None'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\FirewallRules.ps1')
$engine = 'F:\workspace\iphone-mirror\runtime\app\_internal\bin\uxplay.exe'
$session = '0123456789abcdef0123456789abcdef'
$report = [Collections.Generic.List[string]]::new()
function Reset-FakeFirewall {
    $script:store = @{}
    $script:private = $true; $script:enabled = 'True'; $script:localAllowed = 'NotConfigured'
    $script:failProtocol = ''; $script:failDelete = $false; $script:wrongScope = $false; $script:queryFails = $false
}
function Get-NetConnectionProfile { [CmdletBinding()]param(); if ($script:private) { [pscustomobject]@{ NetworkCategory='Private' } } }
function Get-NetFirewallProfile { [CmdletBinding()]param($Name); [pscustomobject]@{ Enabled=$script:enabled; AllowInboundRules='NotConfigured'; AllowLocalFirewallRules=$script:localAllowed } }
function Get-NetFirewallRule {
    [CmdletBinding()]param($PolicyStore,$Name)
    if ($script:queryFails) { Write-Error 'Simulated permission failure' -Category PermissionDenied; return }
    foreach ($key in @($script:store.Keys)) {
        $rule = $script:store[$key]
        if ($rule.Store -eq $PolicyStore -and $rule.Name -like $Name) { $rule }
    }
}
function New-NetFirewallRule {
    [CmdletBinding()]param($PolicyStore,$Name,$DisplayName,$Group,$Direction,$Action,$Enabled,$Program,$Protocol,$LocalPort,$RemoteAddress,$Profile,$EdgeTraversalPolicy)
    if ($Protocol -eq $script:failProtocol) { throw 'Simulated UDP creation failure' }
    if ($PolicyStore -ne 'ActiveStore' -or $EdgeTraversalPolicy -ne 'Block') { throw 'Unexpected persistence or edge traversal' }
    $rule = [pscustomobject]@{ Name=$Name; Store=$PolicyStore; Group=$Group; Direction=$Direction; Action=$Action; Enabled=$Enabled; Program=$Program; Protocol=$Protocol; LocalPort=$LocalPort; RemoteAddress=$RemoteAddress; Profile=$Profile; PolicyStoreSourceType='Dynamic' }
    if ($script:wrongScope) { $rule.RemoteAddress = 'Any' }
    $script:store[$PolicyStore + ':' + $Name] = $rule
    $rule
}
function Get-NetFirewallApplicationFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject); process { [pscustomobject]@{Program=$InputObject.Program} } }
function Get-NetFirewallPortFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject); process { [pscustomobject]@{Protocol=$InputObject.Protocol;LocalPort=$InputObject.LocalPort} } }
function Get-NetFirewallAddressFilter { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject); process { [pscustomobject]@{RemoteAddress=$InputObject.RemoteAddress} } }
function Remove-NetFirewallRule {
    [CmdletBinding()]param($PolicyStore,$Name)
    if ($script:failDelete) { throw 'Simulated cleanup failure' }
    $script:store.Remove($PolicyStore + ':' + $Name)
}
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action,[string]$Contains) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true; Assert ($_.Exception.Message.Contains($Contains)) ('Unexpected error: ' + $_.Exception.Message) }
    Assert $caught 'Expected a failure'
}
function Add-Legacy([string]$Path) {
    foreach ($protocol in @('TCP','UDP')) {
        $name = 'IPhoneMirror-Prototype-' + $protocol
        $script:store['PersistentStore:' + $name] = [pscustomobject]@{ Name=$name; Store='PersistentStore'; Program=$Path }
    }
}
try {
    Reset-FakeFirewall
    $names = @(Open-MirrorFirewall $engine $session)
    Assert ($names.Count -eq 2 -and $script:store.Count -eq 2) 'Expected two narrow dynamic rules'
    Remove-MirrorRules $names $engine
    Assert ($script:store.Count -eq 0) 'Stop left rules behind'
    $report.Add('PASS: start/stop creates exactly two scoped dynamic rules, then verifies removal')

    Reset-FakeFirewall
    Add-Legacy $engine
    $script:store['PersistentStore:unrelated'] = [pscustomobject]@{Name='unrelated'; Store='PersistentStore'; Program='C:\another.exe'}
    $names = @(Open-MirrorFirewall $engine $session)
    Assert ($script:store.Count -eq 3 -and $script:store.ContainsKey('PersistentStore:unrelated')) 'Migration touched unrelated rules'
    $report.Add('PASS: legacy migration preserves unrelated rules')

    Reset-FakeFirewall; Add-Legacy 'C:\different-install\uxplay.exe'
    Expect-Failure { Open-MirrorFirewall $engine $session } 'different installation'
    Assert ($script:store.Count -eq 2) 'Collision changed rules'
    $report.Add('PASS: installation collision rejected without mutation')

    Reset-FakeFirewall; Add-Legacy $engine
    $script:store['PersistentStore:IPhoneMirror-Prototype-UDP'].Program = 'C:\another.exe'
    Expect-Failure { Remove-MirrorRules @('IPhoneMirror-Prototype-TCP','IPhoneMirror-Prototype-UDP') $engine 'PersistentStore' } 'different installation'
    Assert ($script:store.Count -eq 2) 'Removal changed the first rule before checking the second'
    $report.Add('PASS: all ownership checks run before a batch removal')

    Reset-FakeFirewall; $script:failProtocol = 'UDP'
    Expect-Failure { Open-MirrorFirewall $engine $session } 'UDP creation failure'
    Assert ($script:store.Count -eq 0) 'Partial creation did not roll back'
    $report.Add('PASS: second-rule failure rolls back the first rule')

    Reset-FakeFirewall; Add-Legacy $engine; $script:failProtocol = 'UDP'
    Expect-Failure { Open-MirrorFirewall $engine $session } 'UDP creation failure'
    Assert ($script:store.Count -eq 2 -and $script:store.ContainsKey('PersistentStore:IPhoneMirror-Prototype-TCP')) 'Failed startup removed the legacy rules'
    $report.Add('PASS: failed migration preserves the original prototype rules')

    Reset-FakeFirewall; $script:wrongScope = $true
    Expect-Failure { Open-MirrorFirewall $engine $session } 'Unexpected firewall rule scope'
    Assert ($script:store.Count -eq 0) 'Incorrect rule scope was retained'
    $report.Add('PASS: unexpected broad rule rejected and rolled back')

    Reset-FakeFirewall; $script:private = $false
    Expect-Failure { Open-MirrorFirewall $engine $session } 'No Private network'
    Assert ($script:store.Count -eq 0) 'Public-only network opened rules'
    Reset-FakeFirewall; $script:enabled = 'False'
    Expect-Failure { Open-MirrorFirewall $engine $session } 'profile is disabled'
    Reset-FakeFirewall; $script:localAllowed = 'False'
    Expect-Failure { Open-MirrorFirewall $engine $session } 'prohibited'
    $report.Add('PASS: public network, disabled firewall and managed-policy rejection do not change settings')

    Reset-FakeFirewall; $script:queryFails = $true
    Expect-Failure { Open-MirrorFirewall $engine $session } 'permission failure'
    Assert ($script:store.Count -eq 0) 'Query failure was treated as absence'
    $report.Add('PASS: access errors are not mistaken for absent rules')

    Reset-FakeFirewall
    $oldNames = @(Open-MirrorFirewall $engine $session)
    $names = @(Open-MirrorFirewall $engine 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    Assert ($script:store.Count -eq 2 -and -not $script:store.ContainsKey('ActiveStore:' + $oldNames[0])) 'Stale session was not recovered'
    $script:failDelete = $true
    Expect-Failure { Remove-MirrorRules $names $engine } 'cleanup failure'
    $script:failDelete = $false
    Remove-MirrorRules $names $engine
    $report.Add('PASS: stale session recovery and explicit cleanup failure reporting')

    Reset-FakeFirewall
    $names = @(Open-MirrorFirewall $engine $session)
    $script:store['ActiveStore:' + $names[0]].Group = 'some other app'
    Expect-Failure { Open-MirrorFirewall $engine 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' } 'Unexpected rule'
    Assert ($script:store.Count -eq 2) 'Foreign stale rule was removed'
    $report.Add('PASS: unexpected stale rule ownership blocks recovery without deleting it')

    Reset-FakeFirewall
    Expect-Failure { Open-MirrorFirewall $engine 'invalid' } 'Invalid session'
    $report.Add('PASS: invalid session rejected before accessing policy')
    $report.Add('NOT TESTED: real firewall writes, UAC, forced process termination or reboot. No system policy was changed.')
} catch { $report.Add('FAIL: ' + $_.Exception.ToString()); throw }
finally {
    $report
    $destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\firewall-rules-test.txt'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllLines($destination, $report, [Text.UTF8Encoding]::new($false))
}
