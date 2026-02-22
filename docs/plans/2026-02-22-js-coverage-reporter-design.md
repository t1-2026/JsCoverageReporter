# JS Coverage Reporter — Design Document

Date: 2026-02-22

## Overview

A standalone C# console application that uses Playwright.NET to collect JavaScript
coverage from any web page and generates a self-contained HTML report highlighting
which code branches were executed.

## Goals

- Measure JS execution coverage by operating a browser via Playwright.NET
- Define target URL and page interactions in an external JSON config file
- Generate an HTML coverage report with green/red color-coded source lines/ranges
- Distribute as a single self-contained `.exe` (no Node.js, no extra installs)
- One-time prerequisite: `playwright install chromium`

## Non-Goals

- Source map resolution (out of scope for initial version)
- CI/CD integration helpers
- Multi-browser support (Chromium only)

---

## Architecture

```
JsCoverageReporter/
├── JsCoverageReporter.csproj
├── Program.cs                  ← Entry point, CLI argument parsing
├── Config/
│   └── ScenarioConfig.cs       ← JSON config deserialization model
├── Coverage/
│   ├── CoverageCollector.cs    ← Playwright coverage collection logic
│   └── CoverageData.cs         ← V8 coverage data types
├── Actions/
│   └── ActionRunner.cs         ← Executes config-defined page actions
└── Report/
    └── HtmlReportGenerator.cs  ← HTML report generation
```

### CLI Usage

```bash
JsCoverageReporter.exe --config scenario.json --output ./report
```

### NuGet Dependencies

- `Microsoft.Playwright` only

### Distribution

```bash
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true
```

---

## Config File Specification

**Format:** JSON

```json
{
  "url": "https://example.com",
  "scriptFilter": "example.com",
  "actions": [
    { "type": "click",           "selector": "#login-btn" },
    { "type": "fill",            "selector": "input[name=user]", "value": "admin" },
    { "type": "navigate",        "url": "https://example.com/dashboard" },
    { "type": "waitForSelector", "selector": ".loaded" },
    { "type": "hover",           "selector": ".menu" },
    { "type": "wait",            "milliseconds": 1000 }
  ]
}
```

### Fields

| Field          | Required | Description                                              |
|----------------|----------|----------------------------------------------------------|
| `url`          | Yes      | Initial URL to open                                      |
| `scriptFilter` | No       | Only measure scripts whose URL contains this string      |
| `actions`      | No       | Ordered list of page interactions to perform             |

### Action Types

| type              | Required Properties       |
|-------------------|---------------------------|
| `click`           | `selector`                |
| `fill`            | `selector`, `value`       |
| `navigate`        | `url`                     |
| `waitForSelector` | `selector`                |
| `hover`           | `selector`                |
| `wait`            | `milliseconds`            |

---

## Coverage Collection

1. Open Chromium via Playwright (headless)
2. Start JS coverage: `page.Coverage.StartJSCoverageAsync()` with `BlockCoverage = true`
3. Navigate to `url`
4. Execute each action via `ActionRunner`
5. Stop coverage: `page.Coverage.StopJSCoverageAsync()`
6. Filter results by `scriptFilter`

### V8 Coverage Data Structure

Each coverage entry contains:
- `Url` — script URL
- `Source` — full JS source text
- `Functions[]` — list of functions, each with:
  - `Ranges[]` — `{ StartOffset, EndOffset, Count }` (character offsets)

`Count > 0` means the range was executed. Block coverage mode gives per-branch ranges
for if/else, ternary operators, `&&`/`||`, etc.

---

## HTML Report Specification

### Output Structure

```
./report/
├── index.html          ← Summary of all measured scripts
└── scripts/
    ├── script-0.html
    ├── script-1.html
    └── ...
```

### index.html — Summary Table

| Script URL | Executed lines / Total lines | Coverage % | Link |
|---|---|---|---|
| https://example.com/app.js | 42 / 80 | 52.5% | Detail → |

### script-N.html — Detail View

- Line number gutter on the left
- Source code with character-range level color highlighting
- Embedded CSS (no external dependencies, fully self-contained HTML)

### Color Coding

| Color      | Meaning                          |
|------------|----------------------------------|
| Green bg   | Executed code range (count > 0)  |
| Red bg     | Not-executed code range (count = 0) |
| White bg   | Outside coverage scope           |

### HTML Generation Algorithm

1. Build a `int[]` array indexed by character offset, initialized to `-1` (out of scope)
2. For each function range with `Count == 0`: mark offsets as `0` (uncovered)
3. For each function range with `Count > 0`: mark offsets as `1` (covered)
4. Walk source character by character, emitting `<span class="covered">` or
   `<span class="uncovered">` when coverage state changes
5. Split into lines for gutter rendering
6. Compute per-line coverage: a line is "covered" if any of its characters are covered,
   "uncovered" if all in-scope characters are uncovered, "mixed" otherwise

---

## Prerequisites & Setup

### One-time (per machine)

```bash
playwright install chromium
```

This is unavoidable as Playwright requires browser binaries (~150 MB).
Can be scripted into an install script or first-run detection.

### Build & Distribute

```bash
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -o ./dist
```

Output: single `JsCoverageReporter.exe` (~100 MB with .NET runtime embedded).
