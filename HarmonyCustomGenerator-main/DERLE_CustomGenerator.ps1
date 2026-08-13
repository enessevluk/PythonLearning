param(
    [string]$RustRoot = "C:\RustMapServer\RustServer"
)

$ErrorActionPreference = "Stop"
$managed = Join-Path $RustRoot "RustDedicated_Data\Managed"
if (-not (Test-Path -LiteralPath (Join-Path $managed "Assembly-CSharp.dll"))) {
    throw "Rust Managed klasoru bulunamadi: $managed"
}

dotnet msbuild (Join-Path $PSScriptRoot "CustomGenerator.sln") `
    /t:Rebuild /p:Configuration=Release "/p:ReferencePath=$managed" /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = Join-Path $PSScriptRoot "CustomGenerator\bin\Release\CustomGenerator.dll"
Write-Host "Basarili: $dll"
