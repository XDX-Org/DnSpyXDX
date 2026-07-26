window.dnSpyXdx = window.dnSpyXdx || {};
window.dnSpyXdx.getStoredTheme = function () {
  try { return localStorage.getItem("dnspyxdx.theme"); } catch { return null; }
};
window.dnSpyXdx.applyTheme = function (theme) {
  const selected = theme || "default";
  document.documentElement.dataset.theme = selected;
  try { localStorage.setItem("dnspyxdx.theme", selected); } catch { }
};
window.dnSpyXdx.initExplorerResize = function (explorer, dotNet) {
  if (!explorer || explorer.dataset.resizeReady) return;
  explorer.dataset.resizeReady = "true";
  const handle = explorer.querySelector(".explorer-resizer");
  handle.addEventListener("pointerdown", event => {
    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    document.body.classList.add("resizing-explorer");
    const startX = event.clientX;
    const startWidth = explorer.getBoundingClientRect().width;
    let latestX = startX;
    let animationFrame = 0;
    const applyWidth = () => {
      animationFrame = 0;
      const maximum = window.innerWidth * 0.65;
      const width = Math.max(190, Math.min(maximum, startWidth + latestX - startX));
      explorer.style.width = width + "px";
    };
    const move = moveEvent => {
      latestX = moveEvent.clientX;
      if (!animationFrame) animationFrame = requestAnimationFrame(applyWidth);
    };
    const stop = () => {
      if (animationFrame) {
        cancelAnimationFrame(animationFrame);
        applyWidth();
      }
      document.body.classList.remove("resizing-explorer");
      if (dotNet) dotNet.invokeMethodAsync("ExplorerResized", explorer.getBoundingClientRect().width);
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", stop);
      handle.removeEventListener("pointercancel", stop);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", stop);
    handle.addEventListener("pointercancel", stop);
  });
};
window.dnSpyXdx.initSearchResize = function (panel, dotNet) {
  if (!panel || panel.dataset.resizeReady) return;
  panel.dataset.resizeReady = "true";
  const handle = panel.querySelector(".search-resizer");
  handle.addEventListener("pointerdown", event => {
    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    document.body.classList.add("resizing-search");
    const startY = event.clientY;
    const startHeight = panel.getBoundingClientRect().height;
    const move = moveEvent => {
      const maximum = window.innerHeight * 0.65;
      panel.style.height = Math.max(120, Math.min(maximum, startHeight + startY - moveEvent.clientY)) + "px";
    };
    const stop = () => {
      document.body.classList.remove("resizing-search");
      if (dotNet) dotNet.invokeMethodAsync("SearchPanelResized", panel.getBoundingClientRect().height);
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", stop);
      handle.removeEventListener("pointercancel", stop);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", stop);
    handle.addEventListener("pointercancel", stop);
  });
};
window.dnSpyXdx.initHistoryButtons = function (dotNet) {
  if (window.dnSpyXdx.historyReady) return;
  window.dnSpyXdx.historyReady = true;
  // Mouse 4 / mouse 5. Chromium fires these as buttons 3 and 4; preventing the default on
  // mousedown stops the webview treating them as browser back/forward.
  window.addEventListener("mousedown", event => {
    if (event.button === 3 || event.button === 4) event.preventDefault();
  });
  window.addEventListener("mouseup", event => {
    if (event.button !== 3 && event.button !== 4) return;
    event.preventDefault();
    dotNet.invokeMethodAsync("NavigateHistory", event.button === 4);
  });
  window.addEventListener("keydown", event => {
    if (!event.altKey || event.ctrlKey || event.shiftKey) return;
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    dotNet.invokeMethodAsync("NavigateHistory", event.key === "ArrowRight");
  });
};
window.dnSpyXdx.initPanelHorizontalWheel = function () {
  if (window.dnSpyXdx.panelHorizontalWheelReady) return;
  window.dnSpyXdx.panelHorizontalWheelReady = true;
  window.addEventListener("wheel", event => {
    if (!event.shiftKey) return;
    const panel = event.target.closest(".explorer-tree,.source-viewport,.search-results");
    if (!panel) return;
    event.preventDefault();
    panel.scrollLeft += (event.deltaY || event.deltaX) * 0.5;
  }, { passive: false });
};
window.dnSpyXdx.setSourceScroll = async function (source, top, left) {
  if (!source) return;
  top = Math.max(0, top || 0);
  left = Math.max(0, left || 0);
  for (let frame = 0; frame < 20; frame++) {
    source.scrollTop = top;
    source.scrollLeft = left;
    if ((source.scrollTop > 0 || top === 0) && (source.scrollLeft > 0 || left === 0)) return;
    await new Promise(resolve => requestAnimationFrame(resolve));
  }
};
window.dnSpyXdx.getSourceScroll = function (source) {
  return source ? { scrollTop: source.scrollTop, scrollLeft: source.scrollLeft } : { scrollTop: 0, scrollLeft: 0 };
};
window.dnSpyXdx.scrollSourceToLine = async function (source, line, lineHeight) {
  if (!source) return;
  const top = Math.max(0, line * lineHeight - source.clientHeight / 3);
  // Virtualize learns the total spacer height after its first provider result. Wait for that
  // layout before assigning a deep offset, otherwise the browser clamps scrollTop back to zero.
  for (let frame = 0; frame < 20; frame++) {
    source.scrollTop = top;
    if (source.scrollTop > 0 || top === 0) return;
    await new Promise(resolve => requestAnimationFrame(resolve));
  }
};
window.dnSpyXdx.scrollTreeNodeIntoView = function (row) {
  if (row) row.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "auto" });
};
window.dnSpyXdx.scrollTreeToIndex = async function (tree, index, rowHeight) {
  if (!tree) return;
  const top = Math.max(0, index * rowHeight - tree.clientHeight / 3);
  for (let frame = 0; frame < 20; frame++) {
    tree.scrollTop = top;
    if (tree.scrollTop > 0 || top === 0) return;
    await new Promise(resolve => requestAnimationFrame(resolve));
  }
};
window.dnSpyXdx.initSourceFind = function (source, dotNet) {
  window.dnSpyXdx.sourceFindTarget = { source, dotNet };
  if (window.dnSpyXdx.sourceFindReady) return;
  window.dnSpyXdx.sourceFindReady = true;
  window.addEventListener("keydown", event => {
    if (document.activeElement?.closest(".source-find") && (event.key === "Enter" || event.key === "Escape")) {
      event.preventDefault();
      window.dnSpyXdx.sourceFindTarget?.dotNet.invokeMethodAsync("SourceFindKey", event.key, event.shiftKey);
      return;
    }
    if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== "f") return;
    const target = window.dnSpyXdx.sourceFindTarget;
    if (!target) return;
    event.preventDefault();
    target.dotNet.invokeMethodAsync("OpenFind");
  });
};
window.dnSpyXdx.disposeSourceFind = function (source) {
  if (window.dnSpyXdx.sourceFindTarget?.source === source) window.dnSpyXdx.sourceFindTarget = null;
};
window.dnSpyXdx.initHexView = function (viewport, dotNet) {
  if (!viewport) return;
  window.dnSpyXdx.disposeHexView(viewport);
  let scheduled = false;
  const update = () => {
    scheduled = false;
    dotNet.invokeMethodAsync("HexScrolled", viewport.scrollTop, viewport.clientHeight, viewport.scrollHeight);
  };
  const scroll = () => {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(update);
  };
  viewport._dnSpyXdxHexScroll = scroll;
  viewport._dnSpyXdxHexResize = new ResizeObserver(entries => {
    const width = entries[0]?.contentRect.width;
    if (width > 0) dotNet.invokeMethodAsync("HexResized", width);
  });
  viewport._dnSpyXdxHexResize.observe(viewport);
  viewport.addEventListener("scroll", scroll, { passive: true });
  update();
};
window.dnSpyXdx.disposeHexView = function (viewport) {
  if (!viewport?._dnSpyXdxHexScroll) return;
  viewport.removeEventListener("scroll", viewport._dnSpyXdxHexScroll);
  viewport._dnSpyXdxHexResize?.disconnect();
  delete viewport._dnSpyXdxHexScroll;
  delete viewport._dnSpyXdxHexResize;
};
window.dnSpyXdx.scrollHexToRow = function (viewport, row, totalRows, rowHeight) {
  if (!viewport || totalRows <= 0) return;
  const visibleRows = Math.max(1, Math.ceil(viewport.clientHeight / rowHeight));
  const maximumRow = Math.max(0, totalRows - visibleRows);
  const ratio = Math.min(Math.max(row, 0), maximumRow) / Math.max(1, maximumRow);
  viewport.scrollTop = ratio * Math.max(0, viewport.scrollHeight - viewport.clientHeight);
};
