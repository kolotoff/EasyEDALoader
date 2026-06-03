param(
    [string]$ValidatedDir = "Test\StepCleaner\Data\Validated",
    [string]$ReportDir = "Test\StepCleaner\Data\SilhouetteReport"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")
$testProject = Join-Path $repoRoot "Test\StepCleaner\StepCleaner.Tests.csproj"
$validatedPath = if ([System.IO.Path]::IsPathRooted($ValidatedDir)) { $ValidatedDir } else { Join-Path $repoRoot $ValidatedDir }
$reportPath = if ([System.IO.Path]::IsPathRooted($ReportDir)) { $ReportDir } else { Join-Path $repoRoot $ReportDir }

dotnet build $testProject
if ($LASTEXITCODE -ne 0) {
    throw "StepCleaner test project build failed."
}

dotnet run --project $testProject -- --occt-hlr-report $validatedPath $reportPath
if ($LASTEXITCODE -ne 0) {
    throw "OCCT HLR silhouette report generation failed."
}
