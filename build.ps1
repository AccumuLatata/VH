# Builds Loss Lock against the Quantower installation and copies the DLL
# into Settings\Scripts\Strategies\LossLock. Close Quantower before running
# this, otherwise the DLL is locked.
param(
    [string]$QuantowerRoot = "C:\Quantower"
)

$ErrorActionPreference = "Stop"
$platform = Join-Path $QuantowerRoot "TradingPlatform"
if (-not (Test-Path $platform)) {
    throw "Quantower nicht gefunden unter $QuantowerRoot. Pfad übergeben: .\build.ps1 -QuantowerRoot D:\Quantower"
}

$versionDir = Get-ChildItem $platform -Directory |
    Where-Object { $_.Name -like "v*" } |
    Sort-Object Name -Descending |
    Select-Object -First 1

if ($null -eq $versionDir) {
    throw "Kein TradingPlatform\v* Ordner unter $platform"
}

$dll = Join-Path $versionDir.FullName "bin\TradingPlatform.BusinessLayer.dll"
if (-not (Test-Path $dll)) {
    throw "TradingPlatform.BusinessLayer.dll fehlt: $dll"
}

function Get-StrategyTargetFramework([string]$root) {
    $candidates = @(
        (Join-Path $root "TradingPlatform.runtimeconfig.json"),
        (Join-Path $root "bin\TradingPlatform.runtimeconfig.json")
    )
    $found = Get-ChildItem -LiteralPath $root -Filter *.runtimeconfig.json -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 12
    foreach ($item in $found) {
        $candidates += $item.FullName
    }

    foreach ($path in $candidates) {
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        try {
            $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $tfm = $json.runtimeOptions.tfm
            if ([string]::IsNullOrWhiteSpace($tfm)) {
                continue
            }

            if ($tfm -notlike "*-windows") {
                $tfm = "$tfm-windows"
            }

            return $tfm
        }
        catch {
        }
    }

    return "net10.0-windows"
}

$tfm = Get-StrategyTargetFramework $versionDir.FullName
Write-Host "Quantower $($versionDir.Name), Ziel $tfm"
Write-Host $dll

$project = Join-Path $PSScriptRoot "strategy\LossLock.Strategy.csproj"
dotnet build $project -c Release -p:QuantowerBusinessLayer="$dll" -p:TargetFramework="$tfm"
if ($LASTEXITCODE -ne 0) {
    throw "Build fehlgeschlagen. Quantower $($versionDir.Name) braucht dasselbe .NET wie die Strategie (heute .NET 10 Desktop). SDK: dotnet --list-sdks"
}

$built = Join-Path $PSScriptRoot "strategy\bin\Release\LossLock.dll"
$dest = Join-Path $QuantowerRoot "Settings\Scripts\Strategies\LossLock"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $built (Join-Path $dest "LossLock.dll") -Force
Write-Host "Kopiert nach $dest"
Write-Host "Quantower starten, Strategies Manager öffnen, Loss Lock hinzufügen, Konto wählen, Run."
Write-Host "Im Strategies Manager unter General: Restore strategies state after shutdown aktivieren."
