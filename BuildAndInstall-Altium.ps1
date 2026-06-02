param(
    [string]$Configuration = "Release",
    [string]$AltiumProfile = "C:\ProgramData\Altium\Altium Designer Agile {27B91D77-BC6B-4A2D-86DA-D6EB9D851C8D}",
    [string]$AltiumExe = "D:\Program files\ADAgile\X2.EXE",
    [string]$AltiumProcessName = "X2",
    [switch]$NoLaunch,
    [switch]$NoPauseOnError
)

try {
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-AltiumClosed {
    $processes = @(Get-Process -Name $AltiumProcessName -ErrorAction SilentlyContinue)
    if ($processes.Count -gt 0) {
        $processList = ($processes | ForEach-Object {
            $startTime = try { $_.StartTime } catch { "unknown start time" }
            "PID $($_.Id), started $startTime"
        }) -join "; "

        throw "Altium is running ($processList). Close Altium and run this script again. Installation is allowed only when Altium is closed."
    }
}

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot "EasyEDA-Loader\EasyEDA-Loader.csproj"
$sourceDir = Split-Path -Parent $projectPath
$installDir = Join-Path $AltiumProfile "Extensions\EasyEDA-Loader"
$registryPath = Join-Path $AltiumProfile "Extensions\ExtensionsRegistry.xml"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $registryPath)) {
    throw "Altium extensions registry was not found: $registryPath"
}

if (-not (Test-Path -LiteralPath $AltiumExe)) {
    throw "Altium executable was not found: $AltiumExe"
}

Assert-AltiumClosed

Write-Step "Building EasyEDA Loader ($Configuration)"
dotnet build $projectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

Assert-AltiumClosed

[xml]$projectXml = Get-Content -LiteralPath $projectPath
$targetFramework = @($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    $targetFramework = "net8.0-windows"
}

$buildDir = Join-Path $sourceDir "bin\$Configuration\$targetFramework"
$builtDll = Join-Path $buildDir "EasyEDA-Loader.dll"
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Built DLL was not found: $builtDll"
}

Write-Step "Installing to Altium extension folder"
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path (Join-Path $buildDir "*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.ins") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.rcs") -Destination $installDir -Force

$installedDll = Join-Path $installDir "EasyEDA-Loader.dll"
$assembly = [System.Reflection.AssemblyName]::GetAssemblyName($installedDll)
$version = $assembly.Version.ToString()
$versionGuid = [guid]::NewGuid().ToString("B").ToUpperInvariant()

Write-Step "Updating Altium extension registry"
$registryText = Get-Content -LiteralPath $registryPath -Raw
$pattern = '(?s)(<Item HRID="EasyEDA-Loader".*?<Version>)(.*?)(</Version>.*?<VersionGuid>)(.*?)(</VersionGuid>)'
$replacement = '${1}' + $version + '${3}' + $versionGuid + '${5}'
$updatedRegistryText = [regex]::Replace($registryText, $pattern, $replacement, 1)
if ($updatedRegistryText -eq $registryText) {
    throw "EasyEDA-Loader registry item was not updated; pattern did not match."
}

Set-Content -LiteralPath $registryPath -Value $updatedRegistryText -NoNewline -Encoding utf8

[xml]$registryXml = Get-Content -LiteralPath $registryPath
$registryItems = @($registryXml.Extensions.Item).Count
$nonItemNodes = @($registryXml.Extensions.ChildNodes | Where-Object { $_.LocalName -ne "Item" }).Count
$easyEdaItem = @($registryXml.Extensions.Item | Where-Object { $_.HRID -eq "EasyEDA-Loader" }) | Select-Object -First 1

$hash = (Get-FileHash -LiteralPath $installedDll -Algorithm SHA256).Hash
Write-Host "Installed DLL: $installedDll"
Write-Host "Assembly version: $version"
Write-Host "SHA256: $hash"
Write-Host "Registry Items=$registryItems NonItem=$nonItemNodes EasyEDA=$($easyEdaItem.Version) VersionGuid=$($easyEdaItem.VersionGuid)"

if (-not $NoLaunch) {
    Write-Step "Launching Altium"
    Start-Process -FilePath $AltiumExe
}
}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red

    if (-not $NoPauseOnError) {
        Write-Host ""
        Read-Host "Press Enter to close this window" | Out-Null
    }

    exit 1
}
