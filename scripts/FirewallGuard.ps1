#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int] $OwnerId,
    [Parameter(Mandatory)][long] $OwnerStarted,
    [Parameter(Mandatory)][ValidatePattern('^IPhoneMirror-[a-f0-9]{32}$')][string] $PipeName
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'FirewallRules.ps1')
$root = Split-Path -Parent $PSScriptRoot
$engine = [IO.Path]::GetFullPath((Join-Path $root 'runtime\app\_internal\bin\uxplay.exe'))
$app = Join-Path $root 'iPhoneMirror.exe'
$owner = $null; $receiver = $null; $pipe = $null; $writer = $null; $mutex = $null
$ownsMutex = $false; $ruleNames = @(); $failure = $null
$log = Join-Path $root 'logs\firewall.log'
function Write-GuardLog([string] $Message) {
    try { [IO.File]::AppendAllText($log, [DateTimeOffset]::Now.ToString('o') + ' ' + $Message + [Environment]::NewLine) } catch { }
}
function Send-GuardLine([string] $Message) { $writer.WriteLine($Message); $writer.Flush() }
try {
    if (-not [IO.File]::Exists($engine)) { throw 'Mirroring runtime is missing.' }
    $owner = [Diagnostics.Process]::GetProcessById($OwnerId)
    $null = $owner.Handle # Retain the process object even if its PID is reused.
    if ($owner.HasExited -or $owner.StartTime.ToUniversalTime().Ticks -ne $OwnerStarted -or
        -not [string]::Equals($owner.MainModule.FileName, $app, [StringComparison]::OrdinalIgnoreCase)) { throw 'Owner identity does not match.' }
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(15000)
    $reader = New-Object IO.StreamReader($pipe, [Text.UTF8Encoding]::new($false))
    $writer = New-Object IO.StreamWriter($pipe, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    Send-GuardLine 'HELLO'
    $pending = $reader.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $pending.Wait(100)) {
        if ($owner.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Owner disappeared before initialization.' }
    }
    if ($pending.Result -ne 'OPEN') { throw 'Invalid initialization request.' }
    if ($owner.HasExited) { throw 'Owner exited before opening rules.' }
    $prefix = Get-MirrorPrefix $engine
    $mutex = New-Object Threading.Mutex($false, ('Global\' + $prefix))
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another mirroring session is still running or cleaning up. Please retry shortly.' }
    $token = $PipeName.Substring(13)
    # Retain the intended names before opening, so finally can retry even when
    # partial creation and its first rollback both fail.
    $ruleNames = @(('{0}-{1}-TCP' -f $prefix, $token), ('{0}-{1}-UDP' -f $prefix, $token))
    $null = Open-MirrorFirewall $engine $token
    Write-GuardLog 'Temporary Private/LocalSubnet TCP and UDP 7100-7102 rules applied.'
    Send-GuardLine 'READY'
    $pending = $reader.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $owner.HasExited) {
        if ($receiver -and $receiver.HasExited) { break }
        if (-not $receiver -and [DateTime]::UtcNow -gt $deadline) { throw 'Receiver registration timed out.' }
        if (-not $pending.Wait(100)) { continue }
        $line = $pending.Result
        if ($null -eq $line -or $line -eq 'STOP') { break }
        if (-not $receiver -and $line -match '^BIND ([0-9]+) ([0-9]+)$') {
            $candidate = [Diagnostics.Process]::GetProcessById([int]$Matches[1])
            try {
                $null = $candidate.Handle
                $created = [long]$Matches[2]
                $metadata = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $candidate.Id) -ErrorAction Stop
                if ($candidate.HasExited -or $candidate.StartTime.ToUniversalTime().Ticks -ne $created -or
                    -not [string]::Equals($candidate.MainModule.FileName, $engine, [StringComparison]::OrdinalIgnoreCase) -or
                    $metadata.ParentProcessId -ne $OwnerId) { throw 'Receiver identity does not match.' }
                $receiver = $candidate
            } catch { $candidate.Dispose(); throw }
            Send-GuardLine 'BOUND'
        } else { throw 'Invalid guard request.' }
        $pending = $reader.ReadLineAsync()
    }
} catch { $failure = $_.Exception.Message; Write-GuardLog ('Error: ' + $failure) }
finally {
    # Close the actual listener first, even when the GUI disappears unexpectedly.
    if ($receiver) {
        try { if (-not $receiver.HasExited) { $receiver.Kill(); $null = $receiver.WaitForExit(5000) } }
        catch { $failure = 'Receiver cleanup failed: ' + $_.Exception.Message; Write-GuardLog $failure }
        $receiver.Dispose()
    }
    if ($ruleNames.Count) {
        $clean = $false
        for ($attempt = 0; $attempt -lt 3 -and -not $clean; $attempt++) {
            try { Remove-MirrorRules $ruleNames $engine; $clean = $true }
            catch { Write-GuardLog ('Cleanup attempt failed: ' + $_.Exception.Message); Start-Sleep -Milliseconds 500 }
        }
        if (-not $clean) { $failure = 'Temporary rules could not be removed. Restart the app and approve cleanup.' }
        else { Write-GuardLog 'Temporary rules removed and absence verified.' }
    }
    if ($writer) {
        try {
            if ($failure) { Send-GuardLine ('ERROR ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($failure))) }
            else { Send-GuardLine 'CLOSED' }
        } catch { }
    }
    if ($pipe) { $pipe.Dispose() }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    if ($owner) { $owner.Dispose() }
}
if ($failure) { exit 1 }
