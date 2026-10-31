$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path $compiler)) {
    throw '.NET Framework 4.x compiler was not found.'
}

$output = Join-Path $PSScriptRoot 'GlassDotClock.exe'
$references = @(
    'WPF\PresentationFramework.dll',
    'WPF\PresentationCore.dll',
    'WPF\WindowsBase.dll',
    'System.Xaml.dll',
    'System.Windows.Forms.dll',
    'System.Drawing.dll',
    'System.Xml.dll',
    'System.Core.dll',
    'System.Web.Extensions.dll'
)
$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/win32icon:' + (Join-Path $PSScriptRoot 'GlassDotClock.ico')),
    ('/out:' + $output)
)
foreach ($reference in $references) {
    $arguments += '/reference:' + (Join-Path $framework $reference)
}
$arguments += Join-Path $PSScriptRoot 'GlassDotClock.cs'
$arguments += Join-Path $PSScriptRoot 'WeatherService.cs'

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
Copy-Item -LiteralPath $output -Destination (Join-Path $PSScriptRoot 'GlassDotClock.scr') -Force
Write-Host 'Build complete: GlassDotClock.exe and GlassDotClock.scr'
