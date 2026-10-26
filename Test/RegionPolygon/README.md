# Native region-to-polygon regression

Run against an already running Altium instance with the EasyEDA Loader installed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Test/RegionPolygon/Run-LiveRegression.ps1
```

Exactly one `X2` process must have a visible Altium document window. The runner forwards
three scripting commands through that executable with hidden launcher windows; it
does not install the DLL, restart Altium, save boards, or close documents. Each run
creates a unique unsaved scratch PCB and a unique directory under the system temp
directory. Every fixture/command phase checks the native active PCB filename before
touching objects. Keep the generated scratch board active until the run completes.
The runner prints both names and leaves the scratch board and diagnostics available.

The first phase selects a holed region, a filled disc inside its hole, a bottom-layer
rectangle and a curved region. It requires the `EasyEDA Loader Error` dialog to
contain `Regions with holes`, records its text, and closes only that expected dialog
with `WM_CLOSE`. Sorted native snapshots must match exactly, including object IDs,
names, selection, layer/net, region kind/hole count/bounds, and polygon/region shape
segments. An older DLL that silently accepts the batch fails this phase (red).

The second phase deselects only the holed source and converts the other three regions
through the registered EasyEDA menu command. It compares every copied segment field,
layer and net; verifies the source objects disappeared; and checks that the holed
region, unselected region, selected track, existing selected polygon and nets retain
their identities and data. No extra free regions/cutouts may appear.

Each phase times out after 60 seconds by default (`-TimeoutSeconds` overrides it),
allowing the PCB editor to initialize after an application restart.
Unexpected native dialogs fail the run and remain open for manual inspection. The
runner never responds to crash reporters or performs recovery/relaunch loops.
DelphiScript, PCB server APIs and command registration must be enabled in the running
Altium session. This is a live integration test; it does not mock the production
converter. `PASS.txt` is written only after all assertions pass.
