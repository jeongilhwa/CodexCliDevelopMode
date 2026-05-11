# Network Capture Auto-Scroll Design

**Goal**

네트워크 캡처 리스트가 새 항목을 아래로 누적하고, 사용자가 맨 아래를 보고 있을 때만 자동으로 최신 항목을 따라가게 만든다.

**Current Problem**

- 새 캡처 항목이 리스트 맨 위에 삽입된다.
- 필터링 목록을 다시 구성할 때 스크롤 위치가 안정적으로 유지되지 않는다.
- 사용자가 과거 항목을 읽는 중에도 갱신이 시야를 흔든다.

**Approved Behavior**

- 새 네트워크 항목은 리스트 맨 아래에 추가한다.
- 사용자가 맨 아래 근처에 있으면 새 항목 추가 후 자동으로 마지막 항목까지 스크롤한다.
- 사용자가 중간 지점을 보고 있으면 새 항목이 들어와도 현재 스크롤 위치를 유지한다.
- 선택 항목이 있으면 새 항목 추가만으로 선택을 강제로 바꾸지 않는다.
- 선택이 전혀 없을 때만 기본 선택을 최신 항목으로 둔다.

**Design**

- 리스트 추가/트리밍 규칙을 `NetworkCaptureListSupport`로 분리한다.
- `CollectionView.Scrolled` 이벤트에서 마지막으로 보이는 항목 인덱스를 받아 “맨 아래 고정 상태”를 계산한다.
- `AppendWebViewNetworkEntry`는 아래 순서로 동작한다.
  - 현재 맨 아래 고정 상태를 읽는다.
  - 새 항목을 리스트 끝에 추가한다.
  - 최대 개수를 넘으면 가장 오래된 항목부터 제거한다.
  - 필터링 목록을 다시 구성한다.
  - 고정 상태였다면 마지막 항목으로 스크롤한다.

**Files**

- Modify: `src/CodexDesk.App/MainPage.xaml`
- Modify: `src/CodexDesk.App/MainPage.xaml.cs`
- Create: `src/CodexDesk.App/Features/Browser/Services/NetworkCaptureListSupport.cs`
- Create: `src/CodexDesk.App.Tests/Browser/NetworkCaptureListSupportTests.cs`

**Testing**

- 지원 클래스 단위 테스트로 추가 순서와 최대 개수 트리밍을 검증한다.
- 지원 클래스 단위 테스트로 “맨 아래 고정” 판정을 검증한다.
- 기존 선택 유지 테스트가 계속 통과하는지 확인한다.
