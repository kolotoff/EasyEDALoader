# Internal F3D Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the right-side `f3d.exe`/HWND preview integration with in-process libf3d image previews and a shared dual-preview camera state.

**Architecture:** Keep F3D as the color-correct renderer, but use `f3d_c_api.dll` through `StepF3DRenderLib` instead of launching external viewer windows. `DialogWindow` hosts normal WPF `Image` controls, captures mouse gestures on either image, updates one shared camera state, and re-renders both original and clean STEP previews from that state with stale render suppression.

**Tech Stack:** .NET 8 WPF, libf3d C API, `StepF3DRenderLib`, source-level StepCleaner regression tests.

---

### Task 1: Source Regressions

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Add source assertions**

Assert that `DialogWindow` uses WPF preview images, `F3DProjectionRenderer.CreatePreviewSession`, `RenderInteractivePreview`, and `QueueF3DPreviewRender`, while no longer using `SetParent`, `WaitForMainWindowHandleAsync`, `RegisterRawInputDevices`, or `MirrorF3DRawMouseInput`.

- [ ] **Step 2: Run model-cache tests red**

Run: `dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache`

Expected: FAIL because the current implementation still launches and embeds `f3d.exe`.

### Task 2: In-Process Preview Renderer

**Files:**
- Modify: `StepF3DRenderLib/F3DProjectionRenderer.cs`

- [ ] **Step 1: Add preview camera/session API**

Add `F3DPreviewCameraState`, `F3DPreviewRenderResult`, `F3DPreviewRenderPair`, and `F3DProjectionRenderer.CreatePreviewSession(byte[] originalStepData, byte[] cleanStepData)`.

- [ ] **Step 2: Render through libf3d**

The session loads original and clean STEP buffers once, applies scalar coloring, resets bounds, applies shared azimuth/elevation/pan/zoom via C camera API calls, and returns two raw rendered images.

### Task 3: WPF Preview UI

**Files:**
- Modify: `EasyEDA-Loader/DialogWindow.xaml`
- Modify: `EasyEDA-Loader/DialogWindow.cs`

- [ ] **Step 1: Replace WinForms hosts**

Replace `WindowsFormsHost` elements with WPF `Image` elements that use mouse handlers.

- [ ] **Step 2: Replace process lifecycle**

Remove external process startup, HWND reparenting, raw input registration, and mirrored Win32 mouse-message sync from `DialogWindow`.

- [ ] **Step 3: Add shared camera sync**

Use one camera state for both previews. Left drag orbits, right/middle drag pans, mouse wheel zooms, double-click resets. Queue renders with a monotonically increasing request id so stale renders are ignored.

### Task 4: Docs and Verification

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README**

Document that the loader dialog preview uses in-process `f3d_c_api.dll` and `STEPCLEANER_F3D_LIB`, not external `f3d.exe` windows.

- [ ] **Step 2: Run verification**

Run:

`dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache`

`dotnet build EasyEDA-Loader/EasyEDA-Loader.sln --nologo`

Expected: both commands exit `0`.
