# Builds Hydra.exe with the C# compiler bundled with Windows (.NET Framework 4.x)
param([string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$out = if ($Out) { $Out } else { Join-Path $root 'Hydra.exe' }

# Значок приложения (генерируется: Hydra.exe --make-icon src\hydra.ico)
$icon = Join-Path $root 'src\hydra.ico'
$iconArg = if (Test-Path $icon) { "/win32icon:$icon" } else { "/nologo" }

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /nowarn:1607 $iconArg `
    "/out:$out" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll /r:System.Security.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll $src

if ($LASTEXITCODE -ne 0) { throw "Build failed" }
New-Item -ItemType Directory -Force (Join-Path $root 'cores') | Out-Null
Write-Host "Built: $out"
