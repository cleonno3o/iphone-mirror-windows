$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows x64 .NET Framework compiler is required.' }
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', ('/out:' + (Join-Path $PSScriptRoot 'iPhoneMirror.exe')), ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')), ('/resource:' + (Join-Path $PSScriptRoot 'src\MainWindow.xaml') + ',MainWindow.xaml'))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
$compilerArgs += Join-Path $PSScriptRoot 'src\Program.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\VideoWindow.cs'
$compilerArgs += Join-Path $PSScriptRoot 'src\FirewallSession.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output (Join-Path $PSScriptRoot 'iPhoneMirror.exe')
