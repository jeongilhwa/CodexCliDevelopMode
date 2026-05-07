# Cookie Import Details And Clear Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the cookie modal usable while adding clearable cookie details output and allowing `Apply Cookies` to accept the detailed cookie export format.

**Architecture:** Extend the existing `WebViewCookieExportSupport` helper so one focused service owns both export formatting and structured import parsing. Keep the MAUI UI changes minimal: fixed-height editors, one new clear button, and wiring in `MainPage.Browser.cs` to clear fields and to apply structured cookies when present.

**Tech Stack:** .NET 10, MAUI Blazor Hybrid, MSTest, FluentAssertions

---

### Task 1: Define Structured Cookie Import/Export Behavior

**Files:**
- Modify: `src/CodexDesk.App.Tests/Browser/WebViewCookieExportSupportTests.cs`
- Modify: `src/CodexDesk.App/Features/Browser/Services/WebViewCookieExportSupport.cs`

- [ ] **Step 1: Write the failing tests**

Add tests that require:
- detailed export text without numeric prefixes
- parsing the detailed block format back into cookies with `Domain` and `Path`
- preserving plain `Cookie:` header parsing behavior

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src\CodexDesk.App.Tests\CodexDesk.App.Tests.csproj --filter WebViewCookieExportSupportTests`
Expected: FAIL because the helper does not yet parse the detailed block format and still emits the old details shape.

- [ ] **Step 3: Write minimal implementation**

Implement helper methods in `WebViewCookieExportSupport.cs` to:
- build detail text in the new block format
- parse either plain `name=value; ...` or detailed `Name/Value/Domain/Path` blocks
- expose enough structure for `MainPage.Browser.cs` to apply exact `Domain` and `Path`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src\CodexDesk.App.Tests\CodexDesk.App.Tests.csproj --filter WebViewCookieExportSupportTests`
Expected: PASS

### Task 2: Restore Modal Usability And Add Clear

**Files:**
- Modify: `src/CodexDesk.App/MainPage.xaml`
- Modify: `src/CodexDesk.App/MainPage.Browser.cs`

- [ ] **Step 1: Write the failing tests first where practical**

Keep logic-level tests in `WebViewCookieExportSupportTests.cs` for the clear/apply parsing behavior. UI layout itself will be verified by build plus manual reasoning because there is no current MAUI visual test harness for this modal.

- [ ] **Step 2: Make the UI changes**

Update the modal to:
- add a `Clear` button
- switch the two editors to fixed heights instead of `AutoSize="TextChanges"`
- keep details read-only and scrollable

- [ ] **Step 3: Wire behavior in code-behind**

Update `MainPage.Browser.cs` to:
- use structured parsed cookies during apply when present
- clear both editors and the status text when `Clear` is pressed
- continue copying only the header editor content

- [ ] **Step 4: Run verification**

Run:
- `dotnet test src\CodexDesk.App.Tests\CodexDesk.App.Tests.csproj --filter WebViewCookieExportSupportTests`
- `dotnet build src\CodexDesk.App\CodexDesk.App.csproj -f net10.0-windows10.0.19041.0 -v minimal`

Expected:
- tests PASS
- build succeeds with 0 errors
