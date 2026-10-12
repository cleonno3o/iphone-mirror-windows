[CmdletBinding()]
param([switch] $LiveReceiver)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$exe = Join-Path $PSScriptRoot 'iPhoneMirror.exe'
$render = Start-Process -FilePath $exe -ArgumentList '--render' -WindowStyle Hidden -PassThru -Wait
if ($render.ExitCode -ne 0) { throw 'UI render failed. See logs/application-error.log.' }
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'tests\test-firewall-rules.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Firewall simulation failed.' }
if ($LiveReceiver) {
    $smoke = Start-Process -FilePath $exe -ArgumentList '--smoke' -WindowStyle Hidden -PassThru -Wait
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\smoke-test.txt')
    if ($smoke.ExitCode -ne 0) { throw 'Receiver smoke test failed. See logs/receiver.log.' }
    & (Join-Path $PSScriptRoot 'tests\test-rotation.ps1')
}
Write-Output 'Build, WPF render and simulated firewall lifecycle passed. No real firewall write, forced termination or reboot test was performed.'
