# Network Capture Auto-Scroll Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 네트워크 캡처 리스트가 아래로 누적되고, 사용자가 맨 아래를 보고 있을 때만 자동 스크롤되도록 만든다.

**Architecture:** 리스트 추가/트리밍과 하단 고정 판정을 UI 코드에서 분리해 테스트 가능한 지원 클래스로 만든다. `MainPage`는 해당 지원 클래스를 사용해 `CollectionView`의 스크롤 상태와 자동 스크롤만 연결한다.

**Tech Stack:** .NET MAUI, C#, MSTest, FluentAssertions

---

### Task 1: Support Rules

**Files:**
- Create: `src/CodexDesk.App/Features/Browser/Services/NetworkCaptureListSupport.cs`
- Test: `src/CodexDesk.App.Tests/Browser/NetworkCaptureListSupportTests.cs`

- [ ] Write failing tests for append order, max-entry trimming, and bottom-pinned detection.
- [ ] Run targeted tests and confirm failure because the support type does not exist yet.
- [ ] Implement the minimal support methods used by the tests.
- [ ] Re-run the targeted tests and confirm they pass.

### Task 2: MainPage Wiring

**Files:**
- Modify: `src/CodexDesk.App/MainPage.xaml`
- Modify: `src/CodexDesk.App/MainPage.xaml.cs`

- [ ] Wire `CollectionView.Scrolled` to a handler that updates the bottom-pinned state.
- [ ] Change network entry insertion from top insert to bottom append.
- [ ] Trim oldest entries from the start of the backing list when max count is exceeded.
- [ ] Auto-scroll only when the list was already pinned to the bottom before the append.
- [ ] Keep existing selection when possible and only default-select the newest entry when nothing is selected.

### Task 3: Verification

**Files:**
- Test: `src/CodexDesk.App.Tests/Browser/NetworkCaptureListSupportTests.cs`
- Test: `src/CodexDesk.App.Tests/Browser/NetworkCaptureSelectionSupportTests.cs`

- [ ] Run the targeted browser support tests and confirm they pass.
- [ ] Run the app project build if the targeted tests pass cleanly.
