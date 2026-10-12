$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$exe = Join-Path $PSScriptRoot 'iPhoneMirror.exe'
$render = Start-Process -FilePath $exe -ArgumentList '--render' -WindowStyle Hidden -PassThru -Wait
if ($render.ExitCode -ne 0) { throw 'UI render failed. See logs/application-error.log.' }
$smoke = Start-Process -FilePath $exe -ArgumentList '--smoke' -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\smoke-test.txt')
if ($smoke.ExitCode -ne 0) { throw 'Receiver smoke test failed. See logs/receiver.log.' }
Write-Output 'Build, WPF render, receiver start/response/stop and restart passed. Physical iPhone test remains.'
