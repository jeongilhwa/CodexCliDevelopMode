(() => {
  "use strict";

  const storageKey = "notion-page-prototype-v1";
  const seed = {
    title: "코딩인터뷰 완전분석",
    theme: "dark",
    favorite: false,
    icon: false,
    cover: false,
    blocks: [
      { type: "h2", text: "탄탄한 이력서 작성하기" },
      { type: "p", text: "면접관은 똑독하고 코딩을 할 줄 아는 사람을 찾고 싶어한다. 이력서를 보고 실제 지원자를 추리는 사람들(resume screener) 또한 면접관과 같은 생각을 하고있다." },
      { type: "p", text: "그러니 이력서를 쓸 때는 다음의 두 부분을 강조해야 한다. 테니스를 좋아한다거나, 여행을 즐긴다거나, 카드 마술을 할 줄 아는 것은 크게 드러내 보여줄 필요가 없다. 기술적인 부분과 관계없는 취미를 적으려고 기술적인 부분을 덜어내려고 한다면 다시 한번 생각해보라" },
      { type: "h3", text: "적적한 이력서길이" },
      { type: "p", text: "미국에서는 경력이 십 년 미만일 경우 이력서를 한페이지로 만들도록 권장한ㄷ." },
      { type: "p", text: "그보다 경력이 더 된다면 1.5~ 2페이지로 만들수도 있다." },
      { type: "p", text: "이력서 길이가 길어질 경우 다시한번 생각해봐야한다. 이력서는 짧으면 짧을수록 인상에 남는다" },
      { type: "bullet", text: "구인 담당자는 이력서를 볼때 지정된 시간 이상은 쓰지않는다 (기껏해야 10초).\n인상적인 항목만 적혀있다면 반드시 볼 것이다.\n하지만 그렇지 않은 항목들은 보는 사람의 주의만 산만하게 만들 뿐이다." },
      { type: "bullet", text: "어떤 사람은 긴 이력서를 아예 무시해 버리기도 한다.\n이력서가 길다는 이유로 버려지기를 바라는가?" },
      { type: "p", text: "경험이 너무 많아 도저히 한두 페이지에는 담을 수 없다 생각하는 분이 계신다면, 필자를 믿으라.\n줄일수 있다.\n이력서가 길다고 해서 경험이 많다는 사실을 나타내지 않는다. 그저 여러분이 우선순위를 매기는 법을 모른다는 점을 나타낼뿐이다." },
      { type: "h3", text: "고용이력" },
      { type: "p", text: "이력서에 과거의 직함들을 전부 나열할 필요는 없다. 관련된 고용 이력들만 나열해야 한다. 그중에서도 여러분은 더욱 인상 깊은 지원자로 만들어 주는 것들만 말이다." },
      { type: "h4", text: "인상적으로 보이도록 쓰려면" },
      { type: "p", text: "각 직무별로, 무엇을 서취했는지 다음과 같은 방식으로 말해보가. “Y를 구현해서 X를 성취했고, 그결과 Z를 이루었다.” 사례를보자" },
      { type: "bullet", text: "“분산 캐시를 구현해서 오브젝트렌더링 시간을 75% 줄였고, 그결과 로그인 시간을 10%경감할수 있엇다.”" },
      { type: "p", text: "" },
      { type: "p", text: "다른 식으로 작성한 사례도 보자." },
      { type: "bullet", text: "“windiff에 기반한 새로운 비교 알고리즘을 구현한 결과, 평균 비교 정확도를 1.2에서 1.5로 개성했다”" },
      { type: "p", text: "모든 일을 이런 식으로 쓰지는 못하겠지만, 원칙은 같다. 무엇을 했는지, 어떻게 했는지, 그리고 결과는 어떠하였는지를 적으라. 이상적으로는, 결과를 가급적 ‘측정 가능한’ 형태로 제시하는것이 좋다" }
    ]
  };
  const allowedTypes = new Set(["p", "h2", "h3", "h4", "bullet"]);
  const $ = (id) => document.getElementById(id);
  const icon = (name) => `<svg aria-hidden="true"><use href="#i-${name}"></use></svg>`;
  const newId = () => `b-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
  const cloneSeed = () => ({ ...seed, blocks: seed.blocks.map((b) => ({ ...b, id: newId() })) });
  let state = loadState();
  let sidebarOpen = false;
  let activePopover = null;
  let blockMenuTarget = null;
  let saveTimer = 0;
  let toastTimer = 0;
  let draggedId = null;

  function loadState() {
    try {
      const parsed = JSON.parse(localStorage.getItem(storageKey));
      if (parsed && Array.isArray(parsed.blocks) && typeof parsed.title === "string") {
        return {
          title: parsed.title,
          theme: parsed.theme === "light" ? "light" : "dark",
          favorite: !!parsed.favorite,
          icon: !!parsed.icon,
          cover: !!parsed.cover,
          blocks: parsed.blocks.filter((b) => b && allowedTypes.has(b.type) && typeof b.text === "string").map((b) => ({ id: b.id || newId(), type: b.type, text: b.text }))
        };
      }
    } catch { /* An unavailable or stale store leaves the initial document intact. */ }
    return cloneSeed();
  }

  function scheduleSave() {
    $("savedDot").classList.add("saving");
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      try {
        localStorage.setItem(storageKey, JSON.stringify(state));
        $("savedDot").classList.remove("saving");
      } catch { showToast("브라우저 저장소에 저장할 수 없습니다."); }
    }, 250);
  }

  function showToast(message) {
    const toast = $("toast");
    toast.textContent = message;
    toast.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.classList.remove("show"), 2800);
  }

  function renderChrome() {
    document.body.classList.toggle("theme-dark", state.theme === "dark");
    document.body.classList.toggle("theme-light", state.theme === "light");
    $("pageTitle").textContent = state.title;
    updateTitleLabels();
    $("favoriteButton").setAttribute("aria-pressed", String(state.favorite));
    $("pageIcon").hidden = !state.icon;
    $("pageCover").hidden = !state.cover;
    $("addIconButton").textContent = state.icon ? "아이콘 제거" : "아이콘 추가";
    $("addCoverButton").textContent = state.cover ? "커버 제거" : "커버 추가";
    $("themeButton").innerHTML = `${icon(state.theme === "dark" ? "sun" : "moon")}<span>${state.theme === "dark" ? "밝은 테마" : "어두운 테마"}</span>`;
  }

  function updateTitleLabels() {
    const title = state.title.trim() || "제목 없음";
    $("breadcrumbTitle").textContent = title;
    document.querySelector(".sidebar-page-title").textContent = title;
    document.title = `${title} · 문서 프로토타입`;
  }

  function createBlockElement(block) {
    const row = document.createElement("div");
    row.className = "block";
    row.dataset.blockId = block.id;
    row.dataset.type = block.type;
    const actions = document.createElement("div");
    actions.className = "block-actions";
    actions.innerHTML = `<button type="button" class="add-after" aria-label="아래에 블록 추가" title="아래에 블록 추가">${icon("plus")}</button><button type="button" class="drag-handle" draggable="true" aria-label="블록 유형 변경 또는 끌어서 이동" title="클릭: 유형 변경 · 드래그: 이동">${icon("grip")}</button>`;
    const editable = document.createElement(allowedTypes.has(block.type) && block.type.startsWith("h") ? block.type : "div");
    editable.className = "block-content";
    editable.contentEditable = "true";
    editable.spellcheck = true;
    editable.setAttribute("role", "textbox");
    editable.setAttribute("aria-multiline", "true");
    editable.setAttribute("aria-label", block.type === "bullet" ? "글머리 기호 블록" : "문서 블록");
    editable.dataset.placeholder = "입력하거나 /를 눌러 블록 유형 선택";
    editable.textContent = block.text;
    row.append(actions, editable);
    return row;
  }

  function renderBlocks(focusId = null) {
    const container = $("blocks");
    const fragment = document.createDocumentFragment();
    state.blocks.forEach((block) => fragment.appendChild(createBlockElement(block)));
    container.replaceChildren(fragment);
    renderOutline();
    updateStats();
    if (focusId) focusBlock(focusId);
  }

  function renderOutline() {
    const headings = state.blocks.filter((b) => b.type.startsWith("h") && b.text.trim());
    for (const target of [$("outline"), $("toolsOutline")]) {
      target.replaceChildren();
      headings.forEach((block) => {
        const button = document.createElement("button");
        button.type = "button";
        button.dataset.targetId = block.id;
        button.dataset.type = block.type;
        button.textContent = block.text;
        target.appendChild(button);
      });
    }
  }

  function updateStats() {
    $("charCount").textContent = (state.title + state.blocks.map((b) => b.text).join("")).replace(/\s/g, "").length.toLocaleString("ko-KR");
    $("blockCount").textContent = state.blocks.length.toLocaleString("ko-KR");
  }

  function focusBlock(id, atEnd = false) {
    const row = [...$("blocks").children].find((el) => el.dataset.blockId === id);
    const el = row?.querySelector(".block-content");
    if (!el) return;
    el.focus();
    if (atEnd) {
      const range = document.createRange();
      range.selectNodeContents(el);
      range.collapse(false);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    }
  }

  function getBlock(id) { return state.blocks.find((b) => b.id === id); }
  function getBlockIndex(id) { return state.blocks.findIndex((b) => b.id === id); }

  function insertBlock(afterId, type = "p", text = "") {
    const block = { id: newId(), type, text };
    const index = afterId ? getBlockIndex(afterId) : state.blocks.length - 1;
    state.blocks.splice(index + 1, 0, block);
    renderBlocks(block.id);
    scheduleSave();
    return block;
  }

  function caretOffset(el) {
    const selection = window.getSelection();
    if (!selection?.rangeCount || !el.contains(selection.anchorNode)) return el.innerText.length;
    const before = selection.getRangeAt(0).cloneRange();
    before.selectNodeContents(el);
    before.setEnd(selection.anchorNode, selection.anchorOffset);
    return before.toString().length;
  }

  function openSidebar(open) {
    sidebarOpen = open;
    $("appShell").classList.toggle("sidebar-open", open);
    $("sidebar").setAttribute("aria-hidden", String(!open));
    $("sidebar").inert = !open;
    $("openSidebar").setAttribute("aria-expanded", String(open));
    $("openSidebar").setAttribute("aria-label", open ? "사이드바 닫기" : "사이드바 열기");
  }

  function hidePopovers() {
    for (const id of ["shareMenu", "moreMenu", "blockMenu"]) $(id).hidden = true;
    $("shareButton").setAttribute("aria-expanded", "false");
    $("moreButton").setAttribute("aria-expanded", "false");
    activePopover = null;
  }

  function showPopover(id, anchor) {
    const wasOpen = activePopover === id;
    hidePopovers();
    if (wasOpen) return;
    const menu = $(id);
    menu.hidden = false;
    const rect = anchor.getBoundingClientRect();
    const width = menu.offsetWidth;
    const height = menu.offsetHeight;
    menu.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - width - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(rect.bottom + 5, window.innerHeight - height - 8))}px`;
    activePopover = id;
    if (id === "shareMenu") $("shareButton").setAttribute("aria-expanded", "true");
    if (id === "moreMenu") $("moreButton").setAttribute("aria-expanded", "true");
  }

  function openBlockMenu(anchor, id, mode) {
    if (activePopover === "blockMenu") hidePopovers();
    blockMenuTarget = { id, mode };
    showPopover("blockMenu", anchor);
  }

  function openSearch() {
    hidePopovers();
    $("searchBackdrop").hidden = false;
    $("searchInput").value = "";
    renderSearchResults("");
    $("searchInput").focus();
  }

  function closeSearch() { $("searchBackdrop").hidden = true; }

  function renderSearchResults(query) {
    const results = $("searchResults");
    results.replaceChildren();
    const term = query.trim().toLocaleLowerCase();
    const matches = state.blocks.filter((b) => b.text.toLocaleLowerCase().includes(term)).slice(0, 30);
    if (state.title.toLocaleLowerCase().includes(term)) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "search-result";
      item.dataset.searchTitle = "true";
      item.innerHTML = icon("file") + "<span></span>";
      item.querySelector("span").textContent = state.title || "제목 없음";
      results.appendChild(item);
    }
    matches.forEach((block) => {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "search-result";
      item.dataset.targetId = block.id;
      item.innerHTML = icon("file") + "<span></span>";
      item.querySelector("span").textContent = block.text || "빈 블록";
      results.appendChild(item);
    });
    if (!results.children.length) {
      const empty = document.createElement("div");
      empty.className = "search-empty";
      empty.textContent = "일치하는 블록이 없습니다.";
      results.appendChild(empty);
    }
  }

  function scrollToBlock(id, highlight = false) {
    const row = [...$("blocks").children].find((el) => el.dataset.blockId === id);
    if (!row) return;
    row.scrollIntoView({ behavior: "smooth", block: "center" });
    if (highlight) {
      row.classList.add("search-hit");
      setTimeout(() => row.classList.remove("search-hit"), 1800);
    }
  }

  function openTools(open) {
    $("toolsPanel").classList.toggle("open", open);
    $("toolsPanel").setAttribute("aria-hidden", String(!open));
    $("toolsPanel").inert = !open;
    $("aiButton").setAttribute("aria-expanded", String(open));
    updateStats();
  }

  function exportText() {
    const body = [state.title, "", ...state.blocks.map((b) => `${b.type === "bullet" ? "• " : ""}${b.text}`)].join("\n");
    const blob = new Blob([body], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = "코딩인터뷰-완전분석.txt";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    showToast("텍스트 파일을 저장했습니다.");
  }

  function bindEvents() {
    $("openSidebar").addEventListener("click", () => openSidebar(!sidebarOpen));
    $("closeSidebar").addEventListener("click", () => openSidebar(false));
    $("currentPageLink").addEventListener("click", () => { $("documentScroll").scrollTo({ top: 0, behavior: "smooth" }); if (innerWidth < 800) openSidebar(false); });
    $("sidebarSearch").addEventListener("click", openSearch);
    $("topSearch").addEventListener("click", openSearch);
    $("shareButton").addEventListener("click", (e) => showPopover("shareMenu", e.currentTarget));
    $("moreButton").addEventListener("click", (e) => showPopover("moreMenu", e.currentTarget));
    $("favoriteButton").addEventListener("click", () => { state.favorite = !state.favorite; renderChrome(); scheduleSave(); showToast(state.favorite ? "즐겨찾기에 추가했습니다." : "즐겨찾기에서 제거했습니다."); });
    $("themeButton").addEventListener("click", () => { state.theme = state.theme === "dark" ? "light" : "dark"; renderChrome(); scheduleSave(); hidePopovers(); });
    $("addIconButton").addEventListener("click", () => { state.icon = !state.icon; renderChrome(); scheduleSave(); });
    $("addCoverButton").addEventListener("click", () => { state.cover = !state.cover; renderChrome(); scheduleSave(); });
    $("removeCoverButton").addEventListener("click", () => { state.cover = false; renderChrome(); scheduleSave(); });
    $("endAdd").addEventListener("click", () => insertBlock(state.blocks.at(-1)?.id));
    $("aiButton").addEventListener("click", () => openTools(!$("toolsPanel").classList.contains("open")));
    $("closeTools").addEventListener("click", () => openTools(false));
    $("exportButton").addEventListener("click", () => { hidePopovers(); exportText(); });
    $("resetButton").addEventListener("click", () => {
      hidePopovers();
      if (!confirm("이 문서를 원본 내용으로 되돌릴까요? 현재 편집 내용은 삭제됩니다.")) return;
      state = cloneSeed();
      renderChrome();
      renderBlocks();
      scheduleSave();
      showToast("원본 문서로 되돌렸습니다.");
    });
    $("copyLink").addEventListener("click", async () => {
      hidePopovers();
      try { await navigator.clipboard.writeText(location.href); showToast("현재 주소를 복사했습니다."); }
      catch {
        const input = document.createElement("textarea");
        input.value = location.href;
        document.body.appendChild(input);
        input.select();
        const copied = document.execCommand("copy");
        input.remove();
        showToast(copied ? "현재 주소를 복사했습니다." : "주소를 복사할 수 없습니다.");
      }
    });

    $("pageTitle").addEventListener("input", (e) => { state.title = e.currentTarget.innerText.replace(/\n/g, " "); updateTitleLabels(); updateStats(); scheduleSave(); });
    $("pageTitle").addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      e.preventDefault();
      if (state.blocks.length) focusBlock(state.blocks[0].id);
      else insertBlock(null);
    });
    $("pageTitle").addEventListener("paste", (e) => { e.preventDefault(); document.execCommand("insertText", false, e.clipboardData.getData("text/plain").replace(/\s*\n\s*/g, " ")); });

    $("blocks").addEventListener("input", (e) => {
      const editable = e.target.closest(".block-content");
      const block = getBlock(editable?.parentElement.dataset.blockId);
      if (!block) return;
      block.text = editable.innerText.replace(/\r/g, "");
      if (block.type.startsWith("h")) renderOutline();
      updateStats();
      scheduleSave();
    });
    $("blocks").addEventListener("keydown", (e) => {
      const editable = e.target.closest(".block-content");
      if (!editable) return;
      const row = editable.parentElement;
      const index = getBlockIndex(row.dataset.blockId);
      const block = state.blocks[index];
      if (e.key === "/" && !editable.innerText.trim()) { e.preventDefault(); openBlockMenu(row.querySelector(".drag-handle"), block.id, "convert"); }
      if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        const text = editable.innerText.replace(/\r/g, "");
        const offset = Math.min(caretOffset(editable), text.length);
        block.text = text.slice(0, offset);
        insertBlock(block.id, block.type === "bullet" ? "bullet" : "p", text.slice(offset));
      }
      if (e.key === "Backspace" && !editable.innerText.trim() && index > 0) {
        e.preventDefault();
        state.blocks.splice(index, 1);
        renderBlocks(state.blocks[index - 1].id);
        focusBlock(state.blocks[index - 1].id, true);
        scheduleSave();
      }
    });
    $("blocks").addEventListener("click", (e) => {
      const row = e.target.closest(".block");
      if (!row) return;
      if (e.target.closest(".add-after")) openBlockMenu(row.querySelector(".add-after"), row.dataset.blockId, "add");
      if (e.target.closest(".drag-handle")) openBlockMenu(row.querySelector(".drag-handle"), row.dataset.blockId, "convert");
    });
    $("blocks").addEventListener("dragstart", (e) => {
      if (!e.target.closest(".drag-handle")) { e.preventDefault(); return; }
      const row = e.target.closest(".block");
      draggedId = row.dataset.blockId;
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", draggedId);
      row.classList.add("dragging");
    });
    $("blocks").addEventListener("dragover", (e) => {
      const row = e.target.closest(".block");
      if (!draggedId || !row || row.dataset.blockId === draggedId) return;
      e.preventDefault();
      $("blocks").querySelectorAll(".drag-over").forEach((el) => el.classList.remove("drag-over"));
      row.classList.add("drag-over");
    });
    $("blocks").addEventListener("drop", (e) => {
      e.preventDefault();
      const row = e.target.closest(".block");
      if (row && draggedId && row.dataset.blockId !== draggedId) {
        const from = getBlockIndex(draggedId);
        const to = getBlockIndex(row.dataset.blockId);
        const [moved] = state.blocks.splice(from, 1);
        state.blocks.splice(to, 0, moved);
        renderBlocks();
        scheduleSave();
      }
      draggedId = null;
    });
    $("blocks").addEventListener("dragend", () => { draggedId = null; $("blocks").querySelectorAll(".dragging,.drag-over").forEach((el) => el.classList.remove("dragging", "drag-over")); });
    $("blockMenu").addEventListener("click", (e) => {
      const choice = e.target.closest("[data-block-type]");
      if (!choice || !blockMenuTarget) return;
      const type = choice.dataset.blockType;
      const { id, mode } = blockMenuTarget;
      hidePopovers();
      if (mode === "add") insertBlock(id, type);
      else { const block = getBlock(id); if (block) { block.type = type; renderBlocks(id); scheduleSave(); } }
    });
    for (const target of [$("outline"), $("toolsOutline")]) target.addEventListener("click", (e) => { const id = e.target.closest("[data-target-id]")?.dataset.targetId; if (id) { scrollToBlock(id); if (innerWidth < 800) openSidebar(false); } });
    $("searchInput").addEventListener("input", (e) => renderSearchResults(e.target.value));
    $("searchResults").addEventListener("click", (e) => {
      const button = e.target.closest(".search-result");
      if (!button) return;
      closeSearch();
      if (button.dataset.searchTitle) $("documentScroll").scrollTo({ top: 0, behavior: "smooth" });
      else scrollToBlock(button.dataset.targetId, true);
    });
    $("searchBackdrop").addEventListener("click", (e) => { if (e.target === $("searchBackdrop")) closeSearch(); });
    document.addEventListener("click", (e) => { if (activePopover && !e.target.closest(".popover,#shareButton,#moreButton,.block-actions")) hidePopovers(); });
    document.addEventListener("keydown", (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openSearch(); }
      if (e.key === "Escape") { closeSearch(); hidePopovers(); openTools(false); }
    });
  }

  renderChrome();
  renderBlocks();
  bindEvents();
  $("sidebar").inert = true;
  $("toolsPanel").inert = true;
})();
