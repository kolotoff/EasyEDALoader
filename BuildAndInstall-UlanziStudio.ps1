param(
    [string]$UlanziPluginRoot,
    [switch]$SkipNpmInstall,
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "== $Message =="
}

function Add-Candidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not $Candidates.Contains($expanded)) {
        $Candidates.Add($expanded)
    }
}

function Get-UlanziStudioPluginRootCandidates {
    $candidates = [System.Collections.Generic.List[string]]::new()

    Add-Candidate $candidates $env:ULANZI_STUDIO_PLUGIN_ROOT
    Add-Candidate $candidates (Join-Path $env:APPDATA "Ulanzi\UlanziDeck\Plugins")
    Add-Candidate $candidates (Join-Path $env:APPDATA "Ulanzi\UlanziDeck\System\Plugins")
    Add-Candidate $candidates (Join-Path $env:LOCALAPPDATA "UlanziDeck\Plugins")
    Add-Candidate $candidates (Join-Path $env:PROGRAMDATA "Ulanzi\UlanziDeck\Plugins")
    Add-Candidate $candidates (Join-Path $env:APPDATA "UlanziStudio\plugins")
    Add-Candidate $candidates (Join-Path $env:APPDATA "Ulanzi Studio\plugins")
    Add-Candidate $candidates (Join-Path $env:LOCALAPPDATA "UlanziStudio\plugins")
    Add-Candidate $candidates (Join-Path $env:LOCALAPPDATA "Ulanzi Studio\plugins")
    Add-Candidate $candidates (Join-Path $env:PROGRAMDATA "UlanziStudio\plugins")
    Add-Candidate $candidates (Join-Path $env:PROGRAMDATA "Ulanzi Studio\plugins")
    Add-Candidate $candidates (Join-Path $env:USERPROFILE "Documents\UlanziStudio\plugins")
    Add-Candidate $candidates (Join-Path $env:USERPROFILE "Documents\Ulanzi Studio\plugins")

    $programRoots = @($env:PROGRAMFILES, ${env:PROGRAMFILES(X86)}, $env:LOCALAPPDATA)
    foreach ($programRoot in $programRoots) {
        if ([string]::IsNullOrWhiteSpace($programRoot) -or -not (Test-Path -LiteralPath $programRoot)) {
            continue
        }

        Get-ChildItem -LiteralPath $programRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*Ulanzi*" } |
            ForEach-Object {
                Add-Candidate $candidates (Join-Path $_.FullName "Ulanzi\UlanziDeck\Plugins")
                Add-Candidate $candidates (Join-Path $_.FullName "Ulanzi\UlanziDeck\System\Plugins")
                Add-Candidate $candidates (Join-Path $_.FullName "plugins")
                Add-Candidate $candidates (Join-Path $_.FullName "resources\plugins")
                Add-Candidate $candidates (Join-Path $_.FullName "resources\app\plugins")
            }
    }

    return $candidates
}

function Resolve-UlanziStudioPluginRoot {
    param([string]$ConfiguredRoot)

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        return (New-Item -ItemType Directory -Force -Path $ConfiguredRoot).FullName
    }

    $candidates = @(Get-UlanziStudioPluginRootCandidates)
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    foreach ($candidate in $candidates) {
        $parent = Split-Path -Parent $candidate
        if (-not [string]::IsNullOrWhiteSpace($parent) -and (Test-Path -LiteralPath $parent)) {
            return (New-Item -ItemType Directory -Force -Path $candidate).FullName
        }
    }

    $fallback = Join-Path $env:APPDATA "Ulanzi\UlanziDeck\Plugins"
    return (New-Item -ItemType Directory -Force -Path $fallback).FullName
}

function Test-DirectoryWritable {
    param([string]$Path)

    try {
        $directory = (New-Item -ItemType Directory -Force -Path $Path).FullName
        $probe = Join-Path $directory ".easyedaloader-install-test"
        Set-Content -LiteralPath $probe -Value "test" -Encoding ASCII
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch {
        Write-Warning "Skipping non-writable Ulanzi plugin folder '$Path': $($_.Exception.Message)"
        return $false
    }
}

function Add-InstallRoot {
    param(
        [System.Collections.Generic.List[string]]$Roots,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    if (-not (Test-DirectoryWritable $Path)) {
        return
    }

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not $Roots.Contains($resolved)) {
        $Roots.Add($resolved)
    }
}

function Resolve-UlanziStudioPluginRoots {
    param([string]$ConfiguredRoot)

    $roots = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredRoot)) {
        Add-InstallRoot $roots $ConfiguredRoot
        return $roots
    }

    Add-InstallRoot $roots (Join-Path $env:APPDATA "Ulanzi\UlanziDeck\Plugins")
    Add-InstallRoot $roots (Join-Path $env:APPDATA "Ulanzi\UlanziDeck\System\Plugins")

    if ($roots.Count -gt 0) {
        return $roots
    }

    Add-InstallRoot $roots (Resolve-UlanziStudioPluginRoot $null)
    return $roots
}

function Assert-TargetUnderRoot {
    param(
        [string]$Root,
        [string]$Target
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $targetFull = [System.IO.Path]::GetFullPath($Target).TrimEnd('\') + '\'
    if (-not $targetFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to install outside Ulanzi plugin root. Root='$rootFull' Target='$targetFull'"
    }
}

function Get-UlanziStudioProcesses {
    return @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Ulanzi*" -or $_.ProcessName -eq "UlanziDeck" })
}

function Get-UlanziStudioServices {
    return @(Get-Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*Ulanzi*" -or $_.DisplayName -like "*Ulanzi*" })
}

function Stop-UlanziStudioForInstall {
    $processes = @(Get-UlanziStudioProcesses)
    $services = @(Get-UlanziStudioServices)
    $executablePath = ($processes | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Path) } | Select-Object -First 1 -ExpandProperty Path)
    $runningServices = @($services | Where-Object { $_.Status -eq "Running" })
    $shouldRestart = ($processes.Count -gt 0) -or ($runningServices.Count -gt 0)

    if (-not $shouldRestart) {
        return [pscustomobject]@{
            ShouldRestart = $false
            ExecutablePath = $null
            Services = @()
        }
    }

    Write-Step "Closing Ulanzi Studio"
    foreach ($process in $processes) {
        try {
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$process.CloseMainWindow()
                [void]$process.WaitForExit(5000)
            }

            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
            }
        }
        catch {
            Write-Warning "Could not close process $($process.ProcessName) ($($process.Id)): $($_.Exception.Message)"
        }
    }

    foreach ($service in $runningServices) {
        try {
            Stop-Service -Name $service.Name -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not stop Ulanzi service '$($service.Name)': $($_.Exception.Message)"
        }
    }

    return [pscustomobject]@{
        ShouldRestart = $true
        ExecutablePath = $executablePath
        Services = @($runningServices | Select-Object -ExpandProperty Name)
    }
}

function Restart-UlanziStudio {
    param([pscustomobject]$Session)

    if ($null -eq $Session -or -not $Session.ShouldRestart) {
        return
    }

    Write-Step "Restoring Ulanzi Studio"
    foreach ($serviceName in @($Session.Services)) {
        try {
            Start-Service -Name $serviceName -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not start Ulanzi service '$serviceName': $($_.Exception.Message)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Session.ExecutablePath) -and (Test-Path -LiteralPath $Session.ExecutablePath)) {
        Start-Process -FilePath $Session.ExecutablePath
    }
}

function Install-UlanziPluginPackage {
    param(
        [string]$SourcePluginDir,
        [string]$PluginRoot,
        [string]$PluginPackageName,
        [switch]$SkipNpmInstall
    )

    $targetPluginDir = Join-Path $PluginRoot $PluginPackageName
    Assert-TargetUnderRoot -Root $PluginRoot -Target $targetPluginDir

    Write-Host "Ulanzi plugin root: $PluginRoot"
    Write-Host "Plugin package: $targetPluginDir"

    if (Test-Path -LiteralPath $targetPluginDir) {
        Remove-Item -LiteralPath $targetPluginDir -Recurse -Force
    }

    Copy-Item -LiteralPath $SourcePluginDir -Destination $targetPluginDir -Recurse -Force

    if (-not $SkipNpmInstall) {
        $npm = Get-Command npm -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($npm -ne $null) {
            Push-Location $targetPluginDir
            try {
                $npmOutput = & npm install --omit=dev 2>&1
                $npmExitCode = $LASTEXITCODE
                foreach ($line in $npmOutput) {
                    Write-Host $line
                }

                if ($npmExitCode -ne 0) {
                    throw "npm install failed with exit code $npmExitCode."
                }
            }
            finally {
                Pop-Location
            }
        }
        else {
            Write-Warning "npm was not found. Install Node dependencies manually in '$targetPluginDir' before using the plugin."
        }
    }

    return $targetPluginDir
}

$repoRoot = $PSScriptRoot
$pluginPackageName = "com.ulanzi.easyedaloader.ulanziPlugin"
$sourcePluginDir = Join-Path $repoRoot "UlanziStudioPlugin\$pluginPackageName"

if (-not (Test-Path -LiteralPath $sourcePluginDir)) {
    throw "Ulanzi plugin source folder was not found: $sourcePluginDir"
}

$restartSession = $null
if (-not $NoRestart) {
    $restartSession = Stop-UlanziStudioForInstall
}

Write-Step "Resolving Ulanzi Studio plugin folders"
$resolvedPluginRoots = @(Resolve-UlanziStudioPluginRoots $UlanziPluginRoot)
if ($resolvedPluginRoots.Count -eq 0) {
    throw "No writable Ulanzi Studio plugin folder was found."
}

Write-Step "Installing EasyEDALoader Ulanzi Studio plugin"
$installedPluginDirs = @()
foreach ($resolvedPluginRoot in $resolvedPluginRoots) {
    $installedPluginDirs += Install-UlanziPluginPackage `
        -SourcePluginDir $sourcePluginDir `
        -PluginRoot $resolvedPluginRoot `
        -PluginPackageName $pluginPackageName `
        -SkipNpmInstall:$SkipNpmInstall
}

Restart-UlanziStudio $restartSession

Write-Step "Installed"
foreach ($installedPluginDir in $installedPluginDirs) {
    Write-Host "Installed: $installedPluginDir"
}
Write-Host "Assign 'EasyEDA Loader Dial' to an encoder action in Ulanzi Studio. Use -NoRestart to leave Ulanzi Studio running during future installs."
