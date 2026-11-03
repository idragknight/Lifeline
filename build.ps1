<#
.SYNOPSIS
    Builds Lifeline and installs it to the VibeSkua plugin folder.
#>
$ErrorActionPreference = "Stop"
$ROOT = $PSScriptRoot
$SKUA_COMMIT = "967f07e4b2ed7d0180e761644e1e1319d6e5db9a"   # auqw/skua, Skua 1.4.5
$DEPS = Join-Path $ROOT ".deps\skua"

Write-Host "== Lifeline Build Script (VibeSkua Only) ==" -ForegroundColor Cyan

# --- 1. Download and Build Skua Dependencies ---
if (-not (Test-Path (Join-Path $DEPS ".git"))) {
    Write-Host "== Downloading Skua (for its interface assemblies)..."
    git clone -q https://github.com/auqw/skua.git $DEPS
}
git -C $DEPS fetch -q origin
git -C $DEPS checkout -q -f $SKUA_COMMIT

Write-Host "== Building Skua.Core.Interfaces / Models / Utils"
dotnet build (Join-Path $DEPS "Skua.Core.Interfaces\Skua.Core.Interfaces.csproj") -c Release -p:EnableWindowsTargeting=true -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build Skua.Core.Interfaces" }
dotnet build (Join-Path $DEPS "Skua.Core.Models\Skua.Core.Models.csproj") -c Release -p:EnableWindowsTargeting=true -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build Skua.Core.Models" }
dotnet build (Join-Path $DEPS "Skua.Core.Utils\Skua.Core.Utils.csproj") -c Release -p:EnableWindowsTargeting=true -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build Skua.Core.Utils" }

$LIB = Join-Path $ROOT ".deps\lib"
New-Item -ItemType Directory -Force -Path $LIB | Out-Null
$srcDirs = @(
    (Join-Path $DEPS "Skua.Core.Interfaces\bin\Release"),
    (Join-Path $DEPS "Skua.Core.Models\bin\Release"),
    (Join-Path $DEPS "Skua.Core.Utils\bin\Release")
)
foreach ($src in $srcDirs) {
    if (Test-Path $src) {
        Get-ChildItem -Path $src -Filter "Skua.Core.*.dll" -Recurse | Copy-Item -Destination $LIB -Force
    }
}

# --- 2. Build Lifeline Plugin ---
Write-Host "== Building Lifeline plugin"
dotnet build (Join-Path $ROOT "src\Lifeline.csproj") -c Release -p:SkuaLib=$LIB -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build Lifeline plugin (check XAML errors above)." }
$pluginDll = Join-Path $ROOT "src\bin\Release\net10.0-windows\Lifeline.dll"

# --- 3. Build LifelineHelper ---
Write-Host "== Building LifelineHelper"
dotnet build (Join-Path $ROOT "helper\LifelineHelper.csproj") -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw "Failed to build LifelineHelper." }
$helperDir = Join-Path $ROOT "helper\bin\Release\net10.0-windows"

# --- 4. Install to VibeSkua ---
Write-Host "`nInstalling to VibeSkua..." -ForegroundColor Cyan
$pluginsDir = Join-Path $env:APPDATA "VibeSkua\plugins"
$lifelineDir = Join-Path $pluginsDir "Lifeline"

New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
New-Item -ItemType Directory -Force -Path $lifelineDir | Out-Null

# Copy Plugin DLL
Copy-Item -Path $pluginDll -Destination $pluginsDir -Force
Write-Host "  -> Copied Lifeline.dll to $pluginsDir"

# Copy Helper files
$helperFiles = @("LifelineHelper.exe", "LifelineHelper.dll", "LifelineHelper.runtimeconfig.json", "LifelineHelper.deps.json")
foreach ($file in $helperFiles) {
    $srcFile = Join-Path $helperDir $file
    if (Test-Path $srcFile) {
        Copy-Item -Path $srcFile -Destination $lifelineDir -Force
    }
}
Write-Host "  -> Copied LifelineHelper files to $lifelineDir"

Write-Host "`n== Done!" -ForegroundColor Green
Write-Host "Plugin built at: $pluginDll"
Write-Host "Helper built at: $helperDir"
Write-Host "Installed to:    $pluginsDir"
