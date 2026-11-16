# Downloads the sing-box core (Windows amd64) into .\cores
param([string]$Version = '1.14.4')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$root = $PSScriptRoot
$cores = Join-Path $root 'cores'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("singbox-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $cores, $tmp | Out-Null

$url = "https://github.com/SagerNet/sing-box/releases/download/v$Version/sing-box-$Version-windows-amd64.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile (Join-Path $tmp 'sb.zip') -UseBasicParsing
Expand-Archive (Join-Path $tmp 'sb.zip') -DestinationPath (Join-Path $tmp 'x') -Force
$exe = Get-ChildItem (Join-Path $tmp 'x') -Recurse -Filter sing-box.exe | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $cores 'sing-box.exe') -Force
Remove-Item -Recurse -Force $tmp
& (Join-Path $cores 'sing-box.exe') version
