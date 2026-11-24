param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.8 C# compiler is required.'
}

$build = New-Item -Path (Join-Path $PSScriptRoot 'build') -ItemType Directory -Force
$source = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$manifest = Join-Path $PSScriptRoot 'src\app.manifest'
$options = @(
    '/nologo', '/platform:anycpu', '/optimize+', '/warnaserror+', '/codepage:65001',
    "/win32manifest:$manifest",
    "/win32icon:$(Join-Path $PSScriptRoot 'src\SteamGuard.ico')",
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Security.dll',
    '/reference:System.Xml.dll', '/reference:System.Net.Http.dll'
)

$app = Join-Path $build.FullName 'SteamGuard.exe'
& $compiler @options '/target:winexe' "/out:$app" @source
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

if ($Check) {
    $test = Join-Path $PSScriptRoot 'tests\Smoke.cs'
    $testExe = Join-Path $build.FullName 'Smoke.exe'
    & $compiler @options '/target:exe' '/main:Smoke' "/out:$testExe" @source $test
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test build failed.' }
    & $testExe $build.FullName
    if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
}

$portable = Join-Path $PSScriptRoot 'SteamGuard.exe'
Copy-Item -LiteralPath $app -Destination $portable -Force
Get-Item -LiteralPath $portable
