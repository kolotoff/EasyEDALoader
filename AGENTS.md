# Project Rules

- Scope searches and diffs to affected files; bound diagnostic output before broadening.
- For SVG-only changes, build `EasyEDA-Loader/EasyEDA-Loader.csproj` in Release with `-v:q -clp:ErrorsOnly` and use focused SVG checks. Skip the full `Test/StepCleaner` suite unless STEP or renderer code changed; report any focused-test gap.
- Install or restart Altium only when requested or necessary for live verification. If the PCB is still unavailable after one restore attempt, ask for help rather than relaunching repeatedly.
- Verify one board SVG by parsing its XML. Export the other side only for side-selection or mirroring changes.
- Report phase timings only when measured. In dirty worktrees, stage only task files or hunks and inspect the staged diff before committing.
