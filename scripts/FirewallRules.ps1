# Dot-sourced by the elevated guard. Uses only supported NetSecurity cmdlets.
Set-StrictMode -Version Latest

function Get-MirrorPrefix([string] $Engine) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Engine).ToLowerInvariant())
        'IPhoneMirror-Managed-' + ([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').Substring(0, 16)
    } finally { $hash.Dispose() }
}

function Get-MirrorRules([string] $Name, [string] $Store) {
    $queryErrors = @()
    $rules = @(Get-NetFirewallRule -PolicyStore $Store -Name $Name -ErrorAction SilentlyContinue -ErrorVariable queryErrors)
    foreach ($problem in $queryErrors) {
        if ($problem.CategoryInfo.Category -ne 'ObjectNotFound') { throw $problem }
    }
    $rules
}

function Assert-MirrorOwner($Rule, [string] $Engine) {
    $application = $Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
    if (-not [string]::Equals($application.Program, $Engine, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Rule belongs to a different installation: ' + $Rule.Name)
    }
}

function Remove-MirrorRules([string[]] $Names, [string] $Engine, [string] $Store = 'ActiveStore') {
    # Check every rule before changing any of them. Never remove by display name.
    $rules = @()
    foreach ($name in $Names) {
        $found = @(Get-MirrorRules $name $Store)
        foreach ($rule in $found) { Assert-MirrorOwner $rule $Engine }
        $rules += $found
    }
    foreach ($rule in $rules) {
        Remove-NetFirewallRule -PolicyStore $Store -Name $rule.Name -ErrorAction Stop
    }
    foreach ($name in $Names) {
        if (@(Get-MirrorRules $name $Store).Count) { throw ('Firewall cleanup was not confirmed: ' + $name) }
    }
}

function Open-MirrorFirewall([string] $Engine, [string] $Session) {
    if ($Session -notmatch '^[a-f0-9]{32}$') { throw 'Invalid session identifier.' }
    $profiles = @(Get-NetConnectionProfile -ErrorAction Stop | Where-Object { $_.NetworkCategory -eq 'Private' })
    if (-not $profiles.Count) { throw 'No Private network is active. Select Private only on a trusted home network.' }
    $firewall = Get-NetFirewallProfile -Name Private -ErrorAction Stop
    if ($firewall.Enabled -ne 'True' -or $firewall.AllowInboundRules -eq 'False' -or $firewall.AllowLocalFirewallRules -eq 'False') {
        throw 'The Private firewall profile is disabled or local inbound rules are prohibited. No profile settings were changed.'
    }

    $prefix = Get-MirrorPrefix $Engine
    # The guard holds an installation-wide mutex before reaching this function.
    $stale = @(Get-MirrorRules ($prefix + '-*') 'ActiveStore')
    foreach ($rule in $stale) {
        Assert-MirrorOwner $rule $Engine
        if ($rule.Group -ne 'iPhone Mirror managed session' -or $rule.PolicyStoreSourceType -ne 'Dynamic' -or
            $rule.Name -notmatch ('^' + [regex]::Escape($prefix) + '-[a-f0-9]{32}-(TCP|UDP)$')) {
            throw ('Unexpected rule in the app namespace: ' + $rule.Name)
        }
    }
    if ($stale.Count) { Remove-MirrorRules @($stale | ForEach-Object { $_.Name }) $Engine }

    $legacyNames = @('IPhoneMirror-Prototype-TCP', 'IPhoneMirror-Prototype-UDP')
    foreach ($name in $legacyNames) {
        foreach ($rule in @(Get-MirrorRules $name 'PersistentStore')) { Assert-MirrorOwner $rule $Engine }
    }
    $names = @(('{0}-{1}-TCP' -f $prefix, $Session), ('{0}-{1}-UDP' -f $prefix, $Session))
    try {
        for ($i = 0; $i -lt 2; $i++) {
            $protocol = @('TCP', 'UDP')[$i]
            New-NetFirewallRule -PolicyStore ActiveStore -Name $names[$i] -DisplayName ('iPhone Mirror temporary ' + $protocol) `
                -Group 'iPhone Mirror managed session' -Direction Inbound -Action Allow -Enabled True `
                -Program $Engine -Protocol $protocol -LocalPort '7100-7102' -RemoteAddress LocalSubnet -Profile Private `
                -EdgeTraversalPolicy Block -ErrorAction Stop | Out-Null
            $rule = @(Get-MirrorRules $names[$i] 'ActiveStore')
            if ($rule.Count -ne 1) { throw 'The temporary firewall rule could not be verified.' }
            Assert-MirrorOwner $rule[0] $Engine
            $ports = $rule[0] | Get-NetFirewallPortFilter -ErrorAction Stop
            $addresses = $rule[0] | Get-NetFirewallAddressFilter -ErrorAction Stop
            if ($rule[0].Profile -ne 'Private' -or $rule[0].Enabled -ne 'True' -or $rule[0].Direction -ne 'Inbound' -or
                $rule[0].Action -ne 'Allow' -or $rule[0].PolicyStoreSourceType -ne 'Dynamic' -or
                $ports.Protocol -ne $protocol -or (@($ports.LocalPort) -join ',') -ne '7100-7102' -or
                (@($addresses.RemoteAddress) -join ',') -ne 'LocalSubnet') { throw 'Unexpected firewall rule scope.' }
        }
        # Migrate only the two rules created by the original prototype.
        Remove-MirrorRules $legacyNames $Engine 'PersistentStore'
        return $names
    } catch {
        $failure = $_
        try { Remove-MirrorRules $names $Engine }
        catch { throw ($failure.Exception.Message + '; rollback failed: ' + $_.Exception.Message) }
        throw $failure
    }
}
