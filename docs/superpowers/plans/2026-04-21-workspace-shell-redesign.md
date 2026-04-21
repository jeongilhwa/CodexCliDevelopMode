# Workspace Shell Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 홈 화면을 작업 중심의 탭 기반 워크스페이스 셸로 재구성하고, 보조 기능을 도킹 패널로 옮기며, 중간 강도의 아크릴 블러 스타일을 적용한다.

**Architecture:** 기존 `Home.razor`의 상태와 동작은 유지하면서 UI 구조를 `좌측 도크 + 중앙 탭 작업영역 + 우측 컨텍스트 패널` 셸로 재배치한다. 모달에 있던 일부 설정/도구 UI는 우측 패널 안의 섹션으로 이동하고, 기존 탭/전송/승인/브라우저 관련 상태는 페이지 로컬 상태를 재사용한다.

**Tech Stack:** .NET MAUI, Blazor Hybrid, Razor Components, scoped CSS

---

### Task 1: 현재 홈 화면 상태와 리디자인 범위 고정

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor`
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor.css`

- [ ] **Step 1: 레이아웃 전환에 필요한 상태 지점을 확인한다**

확인 대상:

```text
- 탭 목록과 활성 탭: _tabs, _activeTabId, ActiveTab
- 설정 모달 열림 상태: IsSessionModalOpen, SettingsTab
- 햄버거 메뉴 상태: IsHamburgerOpen
- 매크로/Naver Talk 관련 렌더링 분기
```

- [ ] **Step 2: 현재 구조가 유지되어야 하는 핵심 동작을 체크리스트로 고정한다**

체크리스트:

```text
1. Send / resume / cancel 동작 유지
2. 탭 추가 / 전환 / 닫기 유지
3. approval modal 유지
4. collection result modal 유지
5. macro 실행 및 편집 유지
6. Naver Talk 설정과 수집 동작 유지
```

- [ ] **Step 3: 작업 전 빌드 기준선을 확인한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: 성공 또는 현재 저장소의 기존 오류만 재현되고, 새 UI 작업 전 기준선으로 기록 가능

- [ ] **Step 4: 커밋은 구현 완료 후 기능 단위로 수행한다**

```bash
git status --short
```

Expected: 현재 작업 전후 변경 파일 범위를 비교할 수 있다

### Task 2: 홈 화면 마크업을 워크스페이스 셸 구조로 재배치

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor`

- [ ] **Step 1: 기존 hero 영역, 상태 카드, 햄버거 버튼 렌더링을 제거할 위치를 찾는다**

대상 블록:

```razor
<section class="hero"> ... </section>
@if (IsHamburgerOpen) { ... }
```

- [ ] **Step 2: 중앙 작업영역을 감싸는 셸 마크업을 추가한다**

추가할 구조:

```razor
<section class="workspace-shell">
    <aside class="left-dock">...</aside>
    <section class="workspace-center">...</section>
    @if (IsRightPanelOpen)
    {
        <aside class="right-panel">...</aside>
    }
</section>
```

- [ ] **Step 3: 기존 conversation 패널을 중앙 작업영역 안으로 옮긴다**

유지할 내부 블록:

```razor
<div class="tab-bar">...</div>
<div class="transcript">...</div>
<div class="codex-macro-toolbar">...</div>
<div class="composer">...</div>
```

- [ ] **Step 4: 상단 작업 바를 추가해 상태와 빠른 액션을 축약 표시한다**

추가할 요소:

```razor
<header class="workspace-topbar">
    <div class="workspace-title-group">
        <span class="workspace-kicker">Workspace</span>
        <strong>@StatusLabel</strong>
    </div>
    <div class="workspace-actions">
        <button type="button" class="ghost" @onclick="StartFreshConversation" disabled="@ActiveTab.IsBusy">New Thread</button>
        <button type="button" class="ghost" @onclick="ToggleRightPanel">Tools</button>
    </div>
</header>
```

- [ ] **Step 5: 저장 후 Razor 컴파일이 깨지지 않는지 확인한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: 새 셸 마크업 추가로 인한 Razor 구문 오류가 없어야 함

### Task 3: 좌측 도크와 우측 컨텍스트 패널 상태를 추가한다

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor`

- [ ] **Step 1: 패널 선택 상태용 최소 열거형/문자열 상태를 추가한다**

추가할 상태 예시:

```csharp
private string _activeToolPanel = "session";
private bool IsRightPanelOpen { get; set; } = true;
```

- [ ] **Step 2: 도크 클릭 핸들러를 추가한다**

추가할 메서드 예시:

```csharp
private void SelectToolPanel(string panelId)
{
    _activeToolPanel = panelId;
    IsRightPanelOpen = true;
}

private void ToggleRightPanel()
{
    IsRightPanelOpen = !IsRightPanelOpen;
}
```

- [ ] **Step 3: 좌측 도크 항목을 마크업으로 연결한다**

추가할 항목 예시:

```razor
<button type="button" class="dock-item @(IsToolPanel("session") ? "active" : "")" @onclick='() => SelectToolPanel("session")'>Session</button>
<button type="button" class="dock-item @(IsToolPanel("macros") ? "active" : "")" @onclick='() => SelectToolPanel("macros")'>Macros</button>
<button type="button" class="dock-item @(IsToolPanel("naver") ? "active" : "")" @onclick='() => SelectToolPanel("naver")'>Naver</button>
<button type="button" class="dock-item @(IsToolPanel("tools") ? "active" : "")" @onclick='() => SelectToolPanel("tools")'>Tools</button>
```

- [ ] **Step 4: 우측 패널 내부에 기존 설정 섹션을 분기 렌더링한다**

분기 구조:

```razor
@if (_activeToolPanel == "session") { ... }
else if (_activeToolPanel == "macros") { ... }
else if (_activeToolPanel == "naver") { ... }
else if (_activeToolPanel == "tools") { ... }
```

- [ ] **Step 5: 패널 상태 추가 후 빌드한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: 신규 상태 및 메서드가 Razor에서 모두 해석되고 컴파일 성공

### Task 4: 기존 설정 모달 의존도를 줄이고 패널 기반 UX로 옮긴다

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor`

- [ ] **Step 1: 우측 패널 안으로 session, macros, naver, tools UI를 재배치한다**

이동 대상:

```text
- Workspace Path / Command / Model / AllowWorkspaceWrite
- Macro 목록/편집/추가
- Naver Talk 로그인 및 수집 UI
- 외부 도구 실행 버튼
```

- [ ] **Step 2: 모달은 더 이상 기본 진입점이 아니도록 연동을 바꾼다**

변경 내용:

```csharp
private void OpenSessionModal()
{
    _activeToolPanel = "session";
    IsRightPanelOpen = true;
}
```

- [ ] **Step 3: 불필요해진 설정 탭 바와 모달 내부 중복 버튼을 제거한다**

제거 대상:

```text
- settings-tab-bar
- settings-tab 관련 버튼
- 모달 하단의 중복 New Thread / Cancel / Close 버튼
```

- [ ] **Step 4: approval / collection result 모달은 그대로 유지한다**

유지 대상:

```text
- IsCollectionResultModalOpen
- ActiveTab.IsApprovalModalOpen
```

- [ ] **Step 5: 도구 이동 후 동작 확인용 빌드를 실행한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: 설정 모달 경로를 줄여도 기존 이벤트 핸들러 참조 오류가 없어야 함

### Task 5: 탭 바와 중앙 작업영역의 시각적 위계를 재설계한다

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor.css`

- [ ] **Step 1: hero, status-card, hamburger 계열 스타일을 정리한다**

제거 또는 무력화 대상:

```css
.hero
.eyebrow
h1
.lede
.status-card
.desktop-status-card
.hamburger-button
.hamburger-backdrop
.hamburger-panel
```

- [ ] **Step 2: 워크스페이스 셸 레이아웃 스타일을 추가한다**

추가할 골격:

```css
.workspace-shell {
    display: grid;
    grid-template-columns: 4.5rem minmax(0, 1fr) minmax(20rem, 24rem);
    gap: 1rem;
    min-height: calc(100vh - 4rem);
}
```

- [ ] **Step 3: 탭 바를 브라우저/IDE 스타일로 강화한다**

조정 대상:

```css
.tab-bar
.tab-item
.tab-item.active
.tab-add
.tab-close
```

의도:

```text
- 탭 높이를 살짝 키움
- 활성 탭은 더 밝고 더 앞에 보이게
- 비활성 탭은 평평하게 뒤로 물림
- 바 전체를 유리 패널 상단부처럼 보이게
```

- [ ] **Step 4: transcript와 composer 표면을 더 읽기 쉬운 불투명도로 조정한다**

조정 대상:

```css
.chat-panel
.transcript
.message.user
.message.assistant
.composer textarea
```

- [ ] **Step 5: CSS 변경 후 빌드한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: CSS 수정은 빌드를 깨지 않으며 Hot Reload 없이도 정적 자산으로 반영 가능

### Task 6: 아크릴 블러와 도킹 패널 스타일을 적용한다

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor.css`
- Modify: `src/CodexDesk.App/Components/Layout/MainLayout.razor.css`

- [ ] **Step 1: 배경 레이어를 너무 장식적이지 않게 정리한다**

조정 대상:

```css
.shell-frame
.ambient
.ambient-one
.ambient-two
```

- [ ] **Step 2: 좌측 도크와 우측 패널 아크릴 재질을 정의한다**

추가할 스타일 골격:

```css
.left-dock,
.right-panel,
.workspace-topbar,
.chat-panel {
    backdrop-filter: blur(18px);
    background: rgba(255, 248, 240, 0.62);
    border: 1px solid rgba(18, 38, 45, 0.10);
}
```

- [ ] **Step 3: 도크 버튼, 패널 헤더, 섹션 블록 스타일을 정의한다**

추가할 클래스:

```css
.dock-item
.dock-item.active
.right-panel-header
.tool-section
.tool-section-header
```

- [ ] **Step 4: 다크 모드 대응도 동일 구조로 맞춘다**

추가할 조정:

```css
@media (prefers-color-scheme: dark) {
    .left-dock,
    .right-panel,
    .workspace-topbar,
    .chat-panel { ... }
}
```

- [ ] **Step 5: 스타일 마감 후 빌드한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: 레이아웃/CSS 변경 후 앱이 정상 빌드됨

### Task 7: 반응형과 기능 회귀를 정리한다

**Files:**
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor.css`
- Modify: `src/CodexDesk.App/Components/Pages/Home.razor`

- [ ] **Step 1: 좁은 화면에서 3열 셸이 겹치지 않도록 브레이크포인트를 조정한다**

추가할 반응형 골격:

```css
@media (max-width: 1100px) {
    .workspace-shell {
        grid-template-columns: 4rem minmax(0, 1fr);
    }

    .right-panel {
        position: fixed;
        right: 1rem;
        top: 1rem;
        bottom: 1rem;
        width: min(24rem, calc(100vw - 6rem));
    }
}
```

- [ ] **Step 2: 우측 패널 닫기 버튼과 현재 패널 제목을 넣는다**

추가할 마크업:

```razor
<div class="right-panel-header">
    <div>
        <span class="workspace-kicker">Panel</span>
        <strong>@GetToolPanelTitle()</strong>
    </div>
    <button type="button" class="ghost" @onclick="ToggleRightPanel">Close</button>
</div>
```

- [ ] **Step 3: 탭, 패널, 전송 흐름을 수동 확인한다**

수동 시나리오:

```text
1. 새 탭 생성
2. 탭 전환
3. 탭 닫기
4. 프롬프트 입력 후 전송
5. Tools 패널 열고 닫기
6. Macro 실행
7. Naver Talk 버튼 렌더링 확인
```

- [ ] **Step 4: 최종 빌드를 실행한다**

Run: `dotnet build src/CodexDesk.App/CodexDesk.App.csproj`
Expected: Build succeeded.

- [ ] **Step 5: 구현 단위를 커밋한다**

```bash
git add src/CodexDesk.App/Components/Pages/Home.razor src/CodexDesk.App/Components/Pages/Home.razor.css src/CodexDesk.App/Components/Layout/MainLayout.razor.css docs/superpowers/plans/2026-04-21-workspace-shell-redesign.md
git commit -m "Redesign workspace shell layout"
```

## Self-Review

### Spec coverage

- 중앙 탭 중심 워크스페이스: Task 2, 5
- 좌우 도킹 패널: Task 2, 3, 4, 6
- 중간 강도 블러: Task 5, 6
- 불필요한 제목/소개 제거: Task 2, 5
- 기존 기능 회귀 방지: Task 1, 4, 7

### Placeholder scan

- `TBD`, `TODO` 같은 placeholder 없음
- 각 작업에 수정 파일, 대상 구조, 명령, 기대 결과 포함

### Type consistency

- 우측 패널 상태는 `_activeToolPanel`, `IsRightPanelOpen` 기준으로 통일
- 패널 전환 메서드는 `SelectToolPanel`, `ToggleRightPanel`, `GetToolPanelTitle` 이름 체계로 통일
