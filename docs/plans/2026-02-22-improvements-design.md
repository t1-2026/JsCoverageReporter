# JsCoverageReporter Improvements Design

**Date:** 2026-02-22

**Goal:** Fix four remaining issues identified in post-implementation review.

---

## Changes

### 1. Warning on script source retrieval failure

**Problem:** When `Debugger.getScriptSource` throws a `PlaywrightException`, the script is silently skipped. Users see "0 script(s) captured" with no explanation.

**Fix:** Add `Console.Error.WriteLine` in the catch block with the skipped URL.

**File:** `JsCoverageReporter/Coverage/CoverageCollector.cs`

---

### 2. Global action timeout configuration

**Problem:** All Playwright actions use the default 30-second timeout with no way to configure it.

**Design:**
- Add `int? TimeoutMs` to `ScenarioConfig` (null = use Playwright default)
- Pass `timeoutMs` to `ActionRunner.RunAsync`
- Apply to: `click`, `fill`, `navigate`, `waitForSelector`, `hover`, `press`
- `wait` action (Task.Delay) is not affected

**JSON example:**
```json
{
  "url": "...",
  "timeoutMs": 10000,
  "actions": [...]
}
```

**Files:**
- `JsCoverageReporter/Config/ScenarioConfig.cs` — add `TimeoutMs` property
- `JsCoverageReporter/Actions/ActionRunner.cs` — add `timeoutMs` parameter, apply to options
- `JsCoverageReporter/Program.cs` — pass `scenario.TimeoutMs` to `ActionRunner.RunAsync`

---

### 3. Duplicate CLI argument warning

**Problem:** `--config a.json --config b.json` silently uses `b.json`.

**Fix:** Track whether each flag has already been set; warn on second occurrence. Behavior unchanged (last value wins).

**File:** `JsCoverageReporter/Program.cs`

---

### 4. scriptFilter case-insensitive matching

**Problem:** `url.Contains(scriptFilter)` is case-sensitive. URLs can vary in case depending on OS/server.

**Fix:** Change to `url.Contains(scriptFilter, StringComparison.OrdinalIgnoreCase)`.

**File:** `JsCoverageReporter/Coverage/CoverageCollector.cs`

---

## No new config fields (except timeoutMs)

All other fixes are internal behavior changes with no breaking changes to existing scenario JSON files.
