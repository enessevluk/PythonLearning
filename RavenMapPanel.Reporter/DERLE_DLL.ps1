param(
    [string]$RustRoot = "C:\RustMapServer\RustServer",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "build")
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $RustRoot "RustDedicated_Data\Managed"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$panelAssets = Join-Path $PSScriptRoot "..\RavenMapPanel.Wpf\Assets\HarmonyMods"
$rustMods = Join-Path $RustRoot "HarmonyMods"

function Build-Dll {
    param([string]$Name, [string]$Source, [string[]]$References)
    $output = Join-Path $OutputDirectory $Name
    $arguments = @('/nologo', '/target:library', '/optimize+', '/platform:anycpu', '/utf8output', "/out:$output")
    foreach ($reference in $References) { $arguments += "/reference:$(Join-Path $managed $reference)" }
    $arguments += $Source
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "$Name derlenemedi." }
    Copy-Item -LiteralPath $output -Destination (Join-Path $panelAssets $Name) -Force
    if (Test-Path -LiteralPath $rustMods) { Copy-Item -LiteralPath $output -Destination (Join-Path $rustMods $Name) -Force }
}

foreach ($required in @($compiler, $managed, $panelAssets)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Gerekli yol bulunamadı: $required" }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Build-Dll 'RavenWorldObjectReporterV43.dll' (Join-Path $PSScriptRoot 'RavenWorldObjectReporter.cs') @(
    '0Harmony.dll', 'UnityEngine.CoreModule.dll', 'netstandard.dll'
)
Build-Dll 'RavenGuaranteedPlacement.dll' (Join-Path $PSScriptRoot 'RavenGuaranteedPlacement.cs') @(
    '0Harmony.dll', 'Assembly-CSharp.dll', 'Newtonsoft.Json.dll', 'UnityEngine.CoreModule.dll',
    'Rust.Harmony.dll', 'Rust.World.dll', 'Rust.Data.dll', 'Facepunch.System.dll',
    'Facepunch.UnityEngine.dll', 'Facepunch.Network.dll', 'Rust.Global.dll', 'netstandard.dll'
)

Write-Host "Reporter DLL'leri derlendi ve eşitlendi. Raven v3.5.5 hazır: God Rock kesin + biyom/tier + güvenli custom prefab + güncel Outpost template."
