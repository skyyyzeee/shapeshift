<#
.SYNOPSIS
    Собирает портативный Shapeshift: окно и движок в одной папке, с .NET и Windows App SDK внутри,
    и упаковывает в zip. Ставить на целевой компьютер ничего не нужно.

.EXAMPLE
    .\tools\Publish-Release.ps1
    # → artifacts\Shapeshift-<версия>-win-x64.zip
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutRoot) { $OutRoot = Join-Path $repo 'artifacts' }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
$name = "Shapeshift-$version-win-x64"
$out = Join-Path $OutRoot $name
$zip = Join-Path $OutRoot "$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

function Publish([string] $project, [string[]] $extra) {
    & $dotnet publish (Join-Path $repo $project) -c $Configuration -r win-x64 --self-contained true -o $out --nologo @extra
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
}

# Окно ссылается на движок, чтобы тот лежал рядом. При self-contained публикации эта ссылка копирует движок
# в варианте «нужен установленный .NET» — поэтому движок публикуется вторым, отдельно, и перезаписывает себя
# self-contained-вариантом из той же папки.
Publish 'src\Shapeshift.App\Shapeshift.App.csproj' @('-p:ValidateExecutableReferencesMatchSelfContained=false')
Publish 'src\Shapeshift.Engine\Shapeshift.Engine.csproj' @()

# WinUI без MSIX: скомпилированная разметка (*.xbf) и ресурсы окна (Shapeshift.pri) есть в папке сборки,
# но публикация их не копирует — без них окно не запустится. Берём из сборки, которую только что сделал publish.
$build = Join-Path $repo "src\Shapeshift.App\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64"
Get-ChildItem $build -Recurse -Include *.xbf, Shapeshift.pri | ForEach-Object {
    $target = Join-Path $out $_.FullName.Substring($build.Length).TrimStart('\')
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target -Force
}

# Отладочные символы в портативной сборке не нужны.
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

foreach ($exe in 'Shapeshift.exe', 'Shapeshift.Engine.exe', 'catalog\tweaks.json', 'Shapeshift.pri', 'MainWindow.xbf', 'Pages\SourcePage.xbf', 'presets\privacy.json') {
    if (-not (Test-Path (Join-Path $out $exe))) { throw "missing in the package: $exe" }
}
$engineConfig = Get-Content (Join-Path $out 'Shapeshift.Engine.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $engineConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) {
    throw 'Shapeshift.Engine is not self-contained in the package.'
}

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item $zip).Length / 1MB
Write-Host ("{0} ({1:N0} MB)" -f $zip, $size)
