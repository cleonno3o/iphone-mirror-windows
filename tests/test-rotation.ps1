$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$exe = Join-Path $artifacts 'RotationTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output "/out:$exe" "/win32manifest:$root\app.manifest" (Join-Path $root 'src\VideoWindow.cs') (Join-Path $PSScriptRoot 'RotationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Rotation test build failed.' }
$test = Start-Process -FilePath $exe -ArgumentList ('"' + $root + '"') -WindowStyle Hidden -PassThru -Wait
Get-Content -LiteralPath (Join-Path $artifacts 'rotation-test.txt')
if ($test.ExitCode -ne 0) { throw 'Rotation integration test failed.' }
