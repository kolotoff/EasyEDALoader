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

function Resolve-F3DNativeLibrary {
    $configuredPath = [Environment]::GetEnvironmentVariable("STEPCLEANER_F3D_LIB")
    if (-not [string]::IsNullOrWhiteSpace($configuredPath) -and (Test-Path -LiteralPath $configuredPath)) {
        return (Resolve-Path -LiteralPath $configuredPath).Path
    }

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $candidates = @(
        (Join-Path $programFiles "F3D\bin\f3d_c_api.dll"),
        (Join-Path $programFilesX86 "F3D\bin\f3d_c_api.dll")
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Get-FileVersionOrNull {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $fileVersion = (Get-Item -LiteralPath $Path).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) {
        return $null
    }

    $match = [regex]::Match($fileVersion, '^\d+(\.\d+){1,3}')
    if (-not $match.Success) {
        return $null
    }

    return [version]$match.Value
}

function Install-F3DCompatibleMsvcRuntime {
    param(
        [string]$F3DRuntimeSourceDir,
        [string]$AltiumExecutablePath
    )

    if ([string]::IsNullOrWhiteSpace($F3DRuntimeSourceDir) -or -not (Test-Path -LiteralPath $F3DRuntimeSourceDir)) {
        return
    }

    $sourceMsvcp = Join-Path $F3DRuntimeSourceDir "MSVCP140.dll"
    if (-not (Test-Path -LiteralPath $sourceMsvcp)) {
        return
    }

    $altiumExeDir = Split-Path -Parent $AltiumExecutablePath
    $targetMsvcp = Join-Path $altiumExeDir "MSVCP140.dll"
    $sourceVersion = Get-FileVersionOrNull $sourceMsvcp
    $targetVersion = Get-FileVersionOrNull $targetMsvcp

    if ($sourceVersion -eq $null -or $targetVersion -eq $null) {
        Write-Warning "Could not compare MSVCP140.dll versions. F3D source='$sourceMsvcp' Altium target='$targetMsvcp'."
        return
    }

    if ($targetVersion -ge $sourceVersion) {
        Write-Host "Altium MSVCP140.dll is compatible: $targetVersion"
        return
    }

    $backupDirectory = Join-Path $altiumExeDir "EasyEDA-Loader-MsvcBackup"
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    $backupPath = Join-Path $backupDirectory ("MSVCP140.dll." + (Get-Date -Format "yyyyMMdd-HHmmss") + ".bak")
    Copy-Item -LiteralPath $targetMsvcp -Destination $backupPath -Force
    Copy-Item -LiteralPath $sourceMsvcp -Destination $targetMsvcp -Force

    $installedVersion = Get-FileVersionOrNull $targetMsvcp
    Write-Host "Updated Altium MSVCP140.dll for in-process F3D: $targetVersion -> $installedVersion"
    Write-Host "Altium MSVCP140.dll backup: $backupPath"
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
$helperProjectPath = Join-Path $repoRoot "StepOcctHlr\StepOcctHlr.csproj"
$f3dHelperProjectPath = Join-Path $repoRoot "StepF3DRender\StepF3DRender.csproj"
$sourceDir = Split-Path -Parent $projectPath
$helperSourceDir = Split-Path -Parent $helperProjectPath
$f3dHelperSourceDir = Split-Path -Parent $f3dHelperProjectPath
$installDir = Join-Path $AltiumProfile "Extensions\EasyEDA-Loader"
$registryPath = Join-Path $AltiumProfile "Extensions\ExtensionsRegistry.xml"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $helperProjectPath)) {
    throw "OCCT HLR helper project file was not found: $helperProjectPath"
}

if (-not (Test-Path -LiteralPath $f3dHelperProjectPath)) {
    throw "F3D render helper project file was not found: $f3dHelperProjectPath"
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
    throw "dotnet build failed for EasyEDA Loader with exit code $LASTEXITCODE."
}

Write-Step "Building OCCT HLR helper ($Configuration)"
dotnet build $helperProjectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OCCT HLR helper with exit code $LASTEXITCODE."
}

Write-Step "Building F3D render helper ($Configuration)"
dotnet build $f3dHelperProjectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for F3D render helper with exit code $LASTEXITCODE."
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

[xml]$helperProjectXml = Get-Content -LiteralPath $helperProjectPath
$helperTargetFramework = @($helperProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($helperTargetFramework)) {
    $helperTargetFramework = "net8.0-windows7.0"
}

$helperRuntimeIdentifier = @($helperProjectXml.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)[0]
$helperBuildDir = Join-Path $helperSourceDir "bin\$Configuration\$helperTargetFramework"
if (-not [string]::IsNullOrWhiteSpace($helperRuntimeIdentifier)) {
    $helperBuildDir = Join-Path $helperBuildDir $helperRuntimeIdentifier
}

$builtHelperExe = Join-Path $helperBuildDir "StepOcctHlr.exe"
if (-not (Test-Path -LiteralPath $builtHelperExe)) {
    throw "Built OCCT HLR helper executable was not found: $builtHelperExe"
}

[xml]$f3dHelperProjectXml = Get-Content -LiteralPath $f3dHelperProjectPath
$f3dHelperTargetFramework = @($f3dHelperProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
if ([string]::IsNullOrWhiteSpace($f3dHelperTargetFramework)) {
    $f3dHelperTargetFramework = "net8.0-windows7.0"
}

$f3dHelperRuntimeIdentifier = @($f3dHelperProjectXml.Project.PropertyGroup.RuntimeIdentifier | Where-Object { $_ } | Select-Object -First 1)[0]
$f3dHelperBuildDir = Join-Path $f3dHelperSourceDir "bin\$Configuration\$f3dHelperTargetFramework"
if (-not [string]::IsNullOrWhiteSpace($f3dHelperRuntimeIdentifier)) {
    $f3dHelperBuildDir = Join-Path $f3dHelperBuildDir $f3dHelperRuntimeIdentifier
}

$builtF3DHelperExe = Join-Path $f3dHelperBuildDir "StepF3DRender.exe"
if (-not (Test-Path -LiteralPath $builtF3DHelperExe)) {
    throw "Built F3D render helper executable was not found: $builtF3DHelperExe"
}

$f3dNativeLibraryPath = Resolve-F3DNativeLibrary
$f3dNativeRuntimeSourceDir = $null
if ([string]::IsNullOrWhiteSpace($f3dNativeLibraryPath)) {
    Write-Warning "F3D native library f3d_c_api.dll was not found. Install F3D or set STEPCLEANER_F3D_LIB if internal STEP preview is needed."
} else {
    $f3dNativeRuntimeSourceDir = Split-Path -Parent $f3dNativeLibraryPath
}

Write-Step "Installing to Altium extension folder"
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -Path (Join-Path $buildDir "*") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.ins") -Destination $installDir -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "EasyEDA-Loader.rcs") -Destination $installDir -Force

$helperInstallDir = Join-Path $installDir "StepOcctHlr"
New-Item -ItemType Directory -Path $helperInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $helperBuildDir "*") -Destination $helperInstallDir -Recurse -Force

$f3dHelperInstallDir = Join-Path $installDir "StepF3DRender"
New-Item -ItemType Directory -Path $f3dHelperInstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $f3dHelperBuildDir "*") -Destination $f3dHelperInstallDir -Recurse -Force

$f3dNativeInstallDir = Join-Path $installDir "F3D\bin"
$installedF3DNativeLibrary = Join-Path $f3dNativeInstallDir "f3d_c_api.dll"
if (-not [string]::IsNullOrWhiteSpace($f3dNativeRuntimeSourceDir)) {
    New-Item -ItemType Directory -Path $f3dNativeInstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $f3dNativeRuntimeSourceDir "*") -Destination $f3dNativeInstallDir -Recurse -Force

    if (-not (Test-Path -LiteralPath $installedF3DNativeLibrary)) {
        throw "Installed F3D native library was not found: $installedF3DNativeLibrary"
    }

    Install-F3DCompatibleMsvcRuntime -F3DRuntimeSourceDir $f3dNativeRuntimeSourceDir -AltiumExecutablePath $AltiumExe
}

$installedDll = Join-Path $installDir "EasyEDA-Loader.dll"
$installedHelperExe = Join-Path $helperInstallDir "StepOcctHlr.exe"
if (-not (Test-Path -LiteralPath $installedHelperExe)) {
    throw "Installed OCCT HLR helper executable was not found: $installedHelperExe"
}

$installedF3DHelperExe = Join-Path $f3dHelperInstallDir "StepF3DRender.exe"
if (-not (Test-Path -LiteralPath $installedF3DHelperExe)) {
    throw "Installed F3D render helper executable was not found: $installedF3DHelperExe"
}

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
$helperHash = (Get-FileHash -LiteralPath $installedHelperExe -Algorithm SHA256).Hash
$f3dHelperHash = (Get-FileHash -LiteralPath $installedF3DHelperExe -Algorithm SHA256).Hash
$f3dNativeHash = $null
if (Test-Path -LiteralPath $installedF3DNativeLibrary) {
    $f3dNativeHash = (Get-FileHash -LiteralPath $installedF3DNativeLibrary -Algorithm SHA256).Hash
}
Write-Host "Installed DLL: $installedDll"
Write-Host "Assembly version: $version"
Write-Host "SHA256: $hash"
Write-Host "Installed OCCT HLR helper: $installedHelperExe"
Write-Host "OCCT HLR helper SHA256: $helperHash"
Write-Host "Installed F3D render helper: $installedF3DHelperExe"
Write-Host "F3D render helper SHA256: $f3dHelperHash"
if (-not [string]::IsNullOrWhiteSpace($f3dNativeHash)) {
    Write-Host "Installed F3D native library: $installedF3DNativeLibrary"
    Write-Host "F3D native library SHA256: $f3dNativeHash"
} else {
    Write-Host "Installed F3D native library: not bundled; f3d_c_api.dll was not found"
}
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
