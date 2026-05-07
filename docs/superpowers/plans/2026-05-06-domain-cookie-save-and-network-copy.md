# Domain Cookie Save And Network Copy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add domain-scoped cookie snapshot saving with startup auto-load, and add right-click copy actions for captured network sessions.

**Architecture:** Keep the existing WebView2 browser flow intact and add two focused helpers: one for storing and restoring domain-keyed cookie snapshots, and one for exporting a selected network capture entry into raw request/response text. UI changes stay minimal by adding a `Save Cookies` button in the cookie modal and a secondary-click action sheet for network list items.

**Tech Stack:** .NET 10, .NET MAUI, WebView2, SecureStorage, MSTest, FluentAssertions

---

### Task 1: Add Testable Cookie Snapshot And Network Export Helpers

**Files:**
- Create: `src/CodexDesk.App/Features/Browser/Services/DomainCookieSessionStore.cs`
- Create: `src/CodexDesk.App/Features/Browser/Services/WebViewNetworkSessionExportSupport.cs`
- Modify: `src/CodexDesk.App.Tests/Browser/WebViewCookieExportSupportTests.cs`

- [ ] **Step 1: Write the failing tests**

Add tests that require:
- replacing or appending one domain snapshot inside stored JSON without disturbing other domains
- reading saved domain snapshots back as structured cookies
- formatting a selected network entry into raw HTTP request/response text with request line, headers, body, response line, headers, and body

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src\CodexDesk.App.Tests\CodexDesk.App.Tests.csproj --filter WebViewCookieExportSupportTests -v minimal`
Expected: FAIL because the new helper methods and export formatter do not exist yet.

- [ ] **Step 3: Write minimal implementation**

Implement:
- domain-keyed cookie snapshot serialization and parsing
- a raw network session formatter that uses the existing capture entry fields

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src\CodexDesk.App.Tests\CodexDesk.App.Tests.csproj --filter WebViewCookieExportSupportTests -v minimal`
Expected: PASS

### Task 2: Add Cookie Save Button And Startup Auto-Load

**Files:**
- Modify: `src/CodexDesk.App/MainPage.xaml`
- Modify: `src/CodexDesk.App/MainPage.Browser.cs`
- Modify: `src/CodexDesk.App/MainPage.Windows.cs`
- Modify: `src/CodexDesk.App/Features/Browser/Services/NaverTalkBrowserService.cs`
- Create or modify: `src/CodexDesk.App/Features/Browser/Services/NaverTalkPersistedCookie.cs`

- [ ] **Step 1: Add the UI affordance**

Add a `Save Cookies` button to the cookie modal button row without changing the current apply/get/copy/clear flow.

- [ ] **Step 2: Save current matched cookies for the entered domain**

When the button is clicked:
- resolve the target origin the same way as `Get Current`
- read current WebView2 cookies
- keep only host-matching cookies
- preserve `name/value/domain/path/secure/httpOnly/sameSite/expires`
- save them under the normalized origin/domain key in secure storage

- [ ] **Step 3: Auto-load saved domain snapshots on startup**

When WebView2 is initialized and ready:
- read saved domain snapshots once
- inject them into the WebView2 cookie store before normal browsing continues
- avoid re-running restore repeatedly in the same app session

- [ ] **Step 4: Run verification**

Run: `dotnet build src\CodexDesk.App\CodexDesk.App.csproj -f net10.0-windows10.0.19041.0 -v minimal`
Expected: PASS

### Task 3: Add Network Right-Click Copy Actions

**Files:**
- Modify: `src/CodexDesk.App/MainPage.xaml`
- Modify: `src/CodexDesk.App/MainPage.xaml.cs`
- Modify: `src/CodexDesk.App/Features/Browser/Services/WebViewNetworkSessionExportSupport.cs`

- [ ] **Step 1: Add right-click interaction on network list items**

Attach a secondary-click gesture to each session row so the selected item can open a small action menu.

- [ ] **Step 2: Implement clipboard actions**

Support:
- `Copy URL`
- `Copy Session`

`Copy Session` should export text in this order:
- request line
- request headers
- blank line
- request body
- response line
- response headers
- blank line
- response body

- [ ] **Step 3: Surface status feedback**

Show success/failure using the existing browser status text so the user gets immediate confirmation.

- [ ] **Step 4: Run verification**

Run: `dotnet build src\CodexDesk.App\CodexDesk.App.csproj -f net10.0-windows10.0.19041.0 -v minimal`
Expected: PASS
