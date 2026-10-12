$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$downloads = Join-Path $PSScriptRoot 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

function Get-VerifiedFile([string] $Url, [string] $Path, [string] $Sha256) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path -TimeoutSec 180
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Checksum mismatch: $Path. Remove that downloaded file and retry."
    }
}

$installer = Join-Path $downloads 'uxplay-windows-installer-v1.72.1-3.exe'
Get-VerifiedFile 'https://github.com/leapbtw/uxplay-windows/releases/download/1.72.1-3/uxplay-windows-installer-v1.72.1-3.exe' $installer 'cb45de36d960b27d60dfe16a2a45489bd287d4335d37967f109d8cc4b5372986'
$extractorZip = Join-Path $downloads 'innoextract-643.zip'
Get-VerifiedFile 'https://github.com/UserUnknownFactor/innoextract_win/releases/download/643/innoextract643.zip' $extractorZip 'a61219f066cd240f7dba9043e894087397dfbda0539fb1ec2e59c9e66938d019'
$extractorDirectory = Join-Path $downloads 'innoextract-643'
Expand-Archive -LiteralPath $extractorZip -DestinationPath $extractorDirectory -Force
$extractor = Get-ChildItem -LiteralPath $extractorDirectory -Recurse -Filter innoextract.exe | Select-Object -First 1
if (-not $extractor) { throw 'innoextract.exe is missing.' }
# Extract files only. Do not execute the original installer, its registry actions or service setup.
& $extractor.FullName --extract --silent --output-dir (Join-Path $PSScriptRoot 'runtime') $installer
if ($LASTEXITCODE -ne 0) { throw 'Runtime extraction failed.' }
$engine = Join-Path $PSScriptRoot 'runtime\app\_internal\bin\uxplay.exe'
if (-not (Test-Path -LiteralPath $engine)) { throw 'Runtime package does not contain uxplay.exe.' }
& $engine -v
if ($LASTEXITCODE -ne 0) { throw 'Runtime failed its version check.' }
Write-Output 'Runtime ready. Bonjour Service must already be installed and running.'
