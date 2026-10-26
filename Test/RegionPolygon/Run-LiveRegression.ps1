[CmdletBinding()]
param([ValidateRange(5, 60)][int]$TimeoutSeconds = 60)

$ErrorActionPreference = 'Stop'
$runId = [Guid]::NewGuid().ToString('N')
$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) "RegionPolygonRegression-$runId"
$boardName = "RegionPolygonRegression-$runId.PcbDoc"
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$scriptPath = Join-Path $outputDirectory 'LiveRegression.pas'
$projectPath = Join-Path $outputDirectory 'LiveRegression.PrjScr'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LiveRegression.pas.template') -Raw
$template.Replace('@@BOARD@@', $boardName).Replace('@@OUTPUT@@', $outputDirectory.Replace("'", "''")) |
    Set-Content -LiteralPath $scriptPath -Encoding UTF8
"[Design]`r`nVersion=1.0`r`n[Document1]`r`nDocumentPath=LiveRegression.pas`r`n" |
    Set-Content -LiteralPath $projectPath -Encoding UTF8
Write-Host "Artifacts: $outputDirectory"
Write-Host "Scratch PCB: $boardName (left open and unsaved)"

if (-not ('RegionPolygonRegression.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace RegionPolygonRegression {
 public sealed class Dialog { public IntPtr Handle; public string Title; public string Text; }
 public static class Native {
  private delegate bool EnumCallback(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback cb, IntPtr p);
  [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumCallback cb, IntPtr p);
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  private static string Text(IntPtr h) { var s=new StringBuilder(8192); GetWindowText(h,s,s.Capacity); return s.ToString(); }
  public static int[] MainProcessIds() {
   var result=new HashSet<int>();
   EnumWindows((h,p)=> {
    if(!IsWindowVisible(h)) return true;
    var cls=new StringBuilder(256); GetClassName(h,cls,cls.Capacity);
    if(cls.ToString()=="TDocumentForm") { uint id;GetWindowThreadProcessId(h,out id);result.Add((int)id); }
    return true;
   },IntPtr.Zero);
   var ids=new int[result.Count];result.CopyTo(ids);return ids;
  }
  public static Dialog[] Dialogs(int processId) {
   var result=new List<Dialog>();
   EnumWindows((h,p)=> {
    uint id; GetWindowThreadProcessId(h,out id);
    if (id != processId || !IsWindowVisible(h)) return true;
    var cls=new StringBuilder(256); GetClassName(h,cls,cls.Capacity);
    string name=cls.ToString();
    if (name!="#32770" && name!="TMessageForm" && name!="TShowException") return true;
    var text=new StringBuilder();
    EnumChildWindows(h,(c,q)=> { string t=Text(c); if(t.Length>0) text.AppendLine(t); return true; },IntPtr.Zero);
    result.Add(new Dialog { Handle=h,Title=Text(h),Text=text.ToString() }); return true;
   },IntPtr.Zero);
   return result.ToArray();
  }
 }
}
'@
}

$mainIds = [RegionPolygonRegression.Native]::MainProcessIds()
$main = @(Get-Process X2 -ErrorAction SilentlyContinue | Where-Object { $_.Id -in $mainIds })
if ($main.Count -ne 1) { throw 'Exactly one running Altium X2 process with a visible document window is required. Start Altium yourself, then rerun.' }
$main = $main[0]

function Invoke-ScratchPhase([string]$Procedure, [string]$Marker, [switch]$ExpectHoleError) {
    $priorDialogs = @([RegionPolygonRegression.Native]::Dialogs($main.Id))
    if ($priorDialogs.Count) { throw "An Altium dialog is already open: $($priorDialogs.Title -join '; '). Close it manually before rerunning." }
    # Altium forwards this command line to the existing application. This is not an app restart.
    $arguments = '-RScriptingSystem:RunScript(ProjectName="{0}"|ProcName="{1}>%PROCEDURE%")' -f $projectPath, $scriptPath
    $arguments = $arguments.Replace('%PROCEDURE%', $Procedure)
    Start-Process -FilePath $main.Path -ArgumentList $arguments -WindowStyle Hidden | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $sawHoleError = $false
    $closedHandle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath (Join-Path $outputDirectory 'failure.txt')) {
            throw (Get-Content -LiteralPath (Join-Path $outputDirectory 'failure.txt') -Raw)
        }
        foreach ($dialog in @([RegionPolygonRegression.Native]::Dialogs($main.Id))) {
            if ($dialog.Handle -eq $closedHandle) { continue }
            if ($ExpectHoleError -and -not $sawHoleError -and
                $dialog.Title -eq 'EasyEDA Loader Error' -and $dialog.Text.Contains('Regions with holes')) {
                $dialog.Text | Set-Content -LiteralPath (Join-Path $outputDirectory 'rejection-dialog.txt')
                $sawHoleError = $true
                $closedHandle = $dialog.Handle
                if (-not [RegionPolygonRegression.Native]::PostMessage($dialog.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)) {
                    throw 'Could not close the expected holes rejection dialog.'
                }
            } else {
                throw "Unexpected Altium dialog in ${Procedure}: $($dialog.Title)`n$($dialog.Text). No recovery was attempted."
            }
        }
        if (Test-Path -LiteralPath (Join-Path $outputDirectory $Marker)) {
            if ($ExpectHoleError -and -not $sawHoleError) {
                throw 'RED: The command returned without the required Regions with holes rejection. The installed DLL accepted the unsafe batch.'
            }
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for $Procedure ($Marker). Inspect Altium manually; no recovery was attempted."
}

function Read-Snapshot([string]$Name) {
    $rows = @(Get-Content -LiteralPath (Join-Path $outputDirectory $Name))
    if (-not $rows.Count) { throw "Empty snapshot: $Name" }
    return @($rows | ForEach-Object { ,($_ -split '\|') })
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if (($Expected -join "`n") -cne ($Actual -join "`n")) { throw $Message }
}

function Object-Lines($Rows, [string]$Identity) {
    return @($Rows | Where-Object { $_[1] -eq $Identity } | ForEach-Object { $_ -join '|' } | Sort-Object)
}

function Segment-Lines($Rows, [string]$Kind, [string]$Identity) {
    return @($Rows | Where-Object { $_[0] -eq $Kind -and $_[1] -eq $Identity } |
        Sort-Object { [int]$_[2] } | ForEach-Object { $_[3..10] -join '|' })
}

Invoke-ScratchPhase CreateFixture fixture.ready
$before = Read-Snapshot rejection.before.txt
$sources = @($before | Where-Object { $_[0] -eq 'region' -and $_[2] -in @('HOLED_SOURCE','DISC_SOURCE','BOTTOM_SOURCE','ARC_SOURCE') })
if ($sources.Count -ne 4) { throw 'Fixture must contain all four standalone source regions.' }
if (@($before | Where-Object { $_[0] -eq 'track' }).Count -ne 1 -or
    @($before | Where-Object { $_[0] -eq 'polygon' -and $_[2] -eq 'KEEP_POLYGON' }).Count -ne 1 -or
    @($before | Where-Object { $_[0] -eq 'region' -and $_[2] -eq 'KEEP_REGION' }).Count -ne 1) {
    throw 'Fixture must retain the unrelated track, polygon and unselected region.'
}
foreach ($source in $sources) {
    if ($source[5] -notin @('True', '-1') -or $source[8] -notin @('False', '0')) {
        throw "Fixture source must be selected and standalone: $($source[2])"
    }
    if (($source[2] -eq 'HOLED_SOURCE' -and $source[7] -ne '1') -or
        ($source[2] -ne 'HOLED_SOURCE' -and $source[7] -ne '0')) { throw 'Fixture hole counts are incorrect.' }
}
$disc = @($sources | Where-Object { $_[2] -eq 'DISC_SOURCE' })[0]
if ((Segment-Lines $before rsegment $disc[1]).Count -ne 2) { throw 'Disc fixture did not retain its two curved segments.' }

Invoke-ScratchPhase RejectBatch rejection.returned -ExpectHoleError
Assert-Equal (Get-Content -LiteralPath (Join-Path $outputDirectory 'rejection.before.txt')) `
    (Get-Content -LiteralPath (Join-Path $outputDirectory 'rejection.after.txt')) `
    'Atomic rejection failed: native object identities or data changed.'
Write-Host 'PASS: Holed batch rejected with every native snapshot row unchanged.'

Invoke-ScratchPhase ConvertValid positive.returned
$positiveBefore = Read-Snapshot positive.before.txt
$after = Read-Snapshot positive.after.txt
$originalPolygons = @($positiveBefore | Where-Object { $_[0] -eq 'polygon' })
$newPolygons = @($after | Where-Object { $_[0] -eq 'polygon' -and $_[1] -notin @($originalPolygons | ForEach-Object { $_[1] }) })
if ($newPolygons.Count -ne 3) { throw "Expected three replacement polygons, found $($newPolygons.Count)." }
$valid = @($positiveBefore | Where-Object { $_[0] -eq 'region' -and $_[2] -in @('DISC_SOURCE','BOTTOM_SOURCE','ARC_SOURCE') })
foreach ($source in $valid) {
    if (@($after | Where-Object { $_[0] -eq 'region' -and $_[1] -eq $source[1] }).Count) {
        throw "Converted source still exists: $($source[2])"
    }
    $candidates = @($newPolygons | Where-Object { $_[3] -eq $source[3] -and $_[4] -eq $source[4] })
    if ($candidates.Count -ne 1) { throw "Layer/net was not preserved for $($source[2])." }
    $polygon = $candidates[0]
    if ($polygon[5] -notin @('True','-1')) { throw "Replacement polygon is not selected: $($source[2])" }
    $expectedSegments = @(Segment-Lines $positiveBefore rsegment $source[1])
    if (-not $expectedSegments.Count) { throw "Fixture source has no auditable shape segments: $($source[2])" }
    Assert-Equal $expectedSegments (Segment-Lines $after psegment $polygon[1]) "Outline/arc data changed for $($source[2])."
}
$preserved = @($positiveBefore | Where-Object {
    $_[0] -eq 'net' -or $_[0] -eq 'track' -or $_[0] -eq 'polygon' -or
    ($_[0] -eq 'region' -and $_[2] -in @('HOLED_SOURCE','KEEP_REGION'))
})
foreach ($item in $preserved) {
    Assert-Equal (Object-Lines $positiveBefore $item[1]) (Object-Lines $after $item[1]) "Unrelated object changed: $($item[0]) $($item[2])."
}
$freeRegions = @($after | Where-Object { $_[0] -eq 'region' -and $_[8] -in @('False','0') })
if ($freeRegions.Count -ne 2 -or @($freeRegions | Where-Object { $_[7] -ne '1' -and $_[2] -eq 'HOLED_SOURCE' }).Count) {
    throw 'Expected only the original holed region and unselected region after conversion, without new cutouts.'
}
'PASS: Atomic rejection; selected disc, bottom layer and curved outlines converted; nets, layers, segments and unrelated objects preserved.' |
    Tee-Object -FilePath (Join-Path $outputDirectory 'PASS.txt')
