$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$exe = Join-Path $PSScriptRoot 'iPhoneMirror.exe'
$render = Start-Process -FilePath $exe -ArgumentList '--render' -WindowStyle Hidden -PassThru -Wait
if ($render.ExitCode -ne 0) { throw 'UI render failed. See logs/application-error.log.' }
$smoke = Start-Process -FilePath $exe -ArgumentList '--smoke' -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\smoke-test.txt')
if ($smoke.ExitCode -ne 0) { throw 'Receiver smoke test failed. See logs/receiver.log.' }
& (Join-Path $PSScriptRoot 'tests\test-rotation.ps1')
Write-Output 'Build, WPF render, receiver start/response/stop, restart and native video window rotation passed. Physical iPhone rotation confirmation remains.'
