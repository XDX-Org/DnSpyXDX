window.dnSpyXdx = window.dnSpyXdx || {};
window.dnSpyXdx.getStoredTheme = function () {
  try { return localStorage.getItem("dnspyxdx.theme"); } catch { return null; }
};
window.dnSpyXdx.applyTheme = function (theme) {
  const selected = theme || "default";
  document.documentElement.dataset.theme = selected;
  try { localStorage.setItem("dnspyxdx.theme", selected); } catch { }
};
window.dnSpyXdx.applyCodeFont = function (font) {
  const selected = (font || "").trim();
  if (!selected) {
    document.documentElement.style.removeProperty("--app-code-font");
    return;
  }
  const escaped = selected.replaceAll("\\", "\\\\").replaceAll('"', '\\"');
  const generic = ["serif", "sans-serif", "monospace", "system-ui", "ui-monospace"].includes(selected.toLowerCase());
  document.documentElement.style.setProperty("--app-code-font", `${generic ? selected : `"${escaped}"`}, "DejaVu Sans Mono", monospace`);
};
window.dnSpyXdx.getSystemFonts = async function () {
  if (!window.queryLocalFonts) return [];
  try {
    const fonts = await window.queryLocalFonts();
    return [...new Set(fonts.map(font => font.family).filter(Boolean))]
      .sort((left, right) => left.localeCompare(right));
  } catch {
    return [];
  }
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
  const limits = () => ({ minimum: 120, maximum: window.innerHeight * 0.65 });
  const setHeight = height => {
    const { minimum, maximum } = limits();
    const next = Math.max(minimum, Math.min(maximum, height));
    panel.style.height = next + "px";
    handle.setAttribute("aria-valuenow", Math.round(next).toString());
    return next;
  };
  handle.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    document.body.classList.add("resizing-search");
    const startY = event.clientY;
    const startHeight = panel.getBoundingClientRect().height;
    const move = moveEvent => setHeight(startHeight + startY - moveEvent.clientY);
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
  handle.addEventListener("keydown", event => {
    if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
    event.preventDefault();
    const direction = event.key === "ArrowUp" ? 1 : -1;
    const height = setHeight(panel.getBoundingClientRect().height + direction * 32);
    if (dotNet) dotNet.invokeMethodAsync("SearchPanelResized", height);
  });
  handle.addEventListener("dblclick", event => {
    event.preventDefault();
    const { maximum } = limits();
    const current = panel.getBoundingClientRect().height;
    let height;
    if (current >= maximum - 8) {
      height = Number.parseFloat(panel.dataset.restoreHeight) || 230;
    } else {
      panel.dataset.restoreHeight = current.toString();
      height = maximum;
    }
    height = setHeight(height);
    if (dotNet) dotNet.invokeMethodAsync("SearchPanelResized", height);
  });
};
window.dnSpyXdx.initDebuggerResize = function (sections) {
  if (!sections || sections._dnSpyXdxDebuggerResize) return;

  const panes = Array.from(sections.querySelectorAll(":scope > .debugger-section"));
  const handles = Array.from(sections.querySelectorAll(":scope > .debugger-section-resizer"));
  const storageKey = "dnspyxdx.debugger-pane-weights";
  const property = index => `--debugger-pane-${index}-weight`;
  const minimumWidths = [110, 150, 190, 170, 180];

  const setWeights = weights => {
    if (!Array.isArray(weights) || weights.length !== panes.length ||
        weights.some(weight => !Number.isFinite(weight) || weight <= 0)) return false;
    weights.forEach((weight, index) => sections.style.setProperty(property(index), `${weight}fr`));
    return true;
  };
  const currentWidths = () => panes.map(pane => pane.getBoundingClientRect().width);
  const save = () => {
    const widths = currentWidths();
    const total = widths.reduce((sum, width) => sum + width, 0);
    if (total <= 0) return;
    try { localStorage.setItem(storageKey, JSON.stringify(widths.map(width => width / total))); } catch { }
  };
  const reset = () => {
    panes.forEach((_, index) => sections.style.removeProperty(property(index)));
    handles.forEach(handle => {
      handle.removeAttribute("aria-valuemin");
      handle.removeAttribute("aria-valuemax");
      handle.removeAttribute("aria-valuenow");
    });
    try { localStorage.removeItem(storageKey); } catch { }
  };
  const beginResize = (handle, startX, pointerId) => {
    const index = handles.indexOf(handle);
    if (index < 0) return null;

    const widths = currentWidths();
    // Lock every current track to a proportional weight before changing the adjacent pair.
    setWeights(widths);
    const leftWidth = widths[index];
    const rightWidth = widths[index + 1];
    const pairWidth = leftWidth + rightWidth;
    let latestX = startX;
    let animationFrame = 0;
    const apply = () => {
      animationFrame = 0;
      const leftMinimum = minimumWidths[index];
      const rightMinimum = minimumWidths[index + 1];
      const left = Math.max(
        leftMinimum,
        Math.min(pairWidth - rightMinimum, leftWidth + latestX - startX));
      sections.style.setProperty(property(index), `${left}fr`);
      sections.style.setProperty(property(index + 1), `${pairWidth - left}fr`);
      handle.setAttribute("aria-valuemin", leftMinimum.toString());
      handle.setAttribute("aria-valuemax", Math.round(pairWidth - rightMinimum).toString());
      handle.setAttribute("aria-valuenow", Math.round(left).toString());
    };
    const move = event => {
      latestX = event.clientX;
      if (!animationFrame) animationFrame = requestAnimationFrame(apply);
    };
    const stop = () => {
      if (animationFrame) {
        cancelAnimationFrame(animationFrame);
        apply();
      }
      document.body.classList.remove("resizing-debugger");
      save();
      if (pointerId !== null && handle.hasPointerCapture?.(pointerId))
        handle.releasePointerCapture(pointerId);
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", stop);
      handle.removeEventListener("pointercancel", stop);
    };
    return { move, stop };
  };

  try {
    const stored = JSON.parse(localStorage.getItem(storageKey));
    setWeights(stored);
  } catch { }

  const listeners = handles.map(handle => {
    const pointerdown = event => {
      if (event.button !== 0) return;
      event.preventDefault();
      handle.setPointerCapture(event.pointerId);
      document.body.classList.add("resizing-debugger");
      const drag = beginResize(handle, event.clientX, event.pointerId);
      if (!drag) return;
      handle.addEventListener("pointermove", drag.move);
      handle.addEventListener("pointerup", drag.stop);
      handle.addEventListener("pointercancel", drag.stop);
    };
    const keydown = event => {
      if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
      event.preventDefault();
      const drag = beginResize(handle, 0, null);
      if (!drag) return;
      drag.move({ clientX: event.key === "ArrowLeft" ? -24 : 24 });
      drag.stop();
    };
    const doubleclick = event => {
      event.preventDefault();
      reset();
    };
    handle.addEventListener("pointerdown", pointerdown);
    handle.addEventListener("keydown", keydown);
    handle.addEventListener("dblclick", doubleclick);
    return { handle, pointerdown, keydown, doubleclick };
  });

  sections._dnSpyXdxDebuggerResize = { listeners };
};
window.dnSpyXdx.disposeDebuggerResize = function (sections) {
  const state = sections?._dnSpyXdxDebuggerResize;
  if (!state) return;
  for (const listener of state.listeners) {
    listener.handle.removeEventListener("pointerdown", listener.pointerdown);
    listener.handle.removeEventListener("keydown", listener.keydown);
    listener.handle.removeEventListener("dblclick", listener.doubleclick);
  }
  document.body.classList.remove("resizing-debugger");
  delete sections._dnSpyXdxDebuggerResize;
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
// Ctrl/Cmd+A selects every assembly and Delete unloads the current selection, but only while focus is inside
// the assembly tree — so these never steal select-all or delete from the source view, search box, or elsewhere.
window.dnSpyXdx.initExplorerKeys = function (dotNet) {
  window.dnSpyXdx.explorerKeysTarget = dotNet;
  if (window.dnSpyXdx.explorerKeysReady) return;
  window.dnSpyXdx.explorerKeysReady = true;
  // The tree only takes keyboard focus once a row is clicked, but dnSpy lets Ctrl+A / Delete act on the
  // assembly list whenever the pointer is over it. Track hover so hovering the tree is enough — every
  // mouseover recomputes whether the cursor is inside it.
  window.addEventListener("mouseover", event => { window.dnSpyXdx.explorerHovered = !!event.target?.closest?.(".explorer-tree"); });
  window.addEventListener("keydown", event => {
    const target = window.dnSpyXdx.explorerKeysTarget;
    if (!target) return;
    const active = document.activeElement;
    // Never steal these keys while typing in a field (e.g. the search box), even if the cursor is over the tree.
    if (active && (active.tagName === "INPUT" || active.tagName === "TEXTAREA" || active.isContentEditable)) return;
    if (!active?.closest?.(".explorer-tree") && !window.dnSpyXdx.explorerHovered) return;
    if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "a") {
      event.preventDefault();
      event.stopPropagation();
      // Selecting assemblies must not also select the page text: cancel the browser's select-all and drop any
      // selection that formed anyway.
      window.getSelection?.()?.removeAllRanges();
      target.invokeMethodAsync("SelectAllAssemblies");
    } else if (event.key === "Delete" && !event.ctrlKey && !event.altKey && !event.shiftKey) {
      event.preventDefault();
      event.stopPropagation();
      target.invokeMethodAsync("UnloadSelectedAssemblies");
    }
  });
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
window.dnSpyXdx.scrollSourceToRenderedLine = function (source, line) {
  source?.querySelector(`[data-source-line="${line}"]`)?.scrollIntoView({ block: "center", inline: "nearest", behavior: "auto" });
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
// Highlights every occurrence of the symbol under the cursor, entirely in the DOM. This used to be driven
// from .NET (a mouseenter/mouseleave callback per token that re-rendered the whole source view); during a
// scroll the cursor sweeps across many tokens and that flooded the WebView bridge until it locked up. A
// single delegated listener toggling a class avoids any round-trip or Blazor re-render.
window.dnSpyXdx.initSourceHover = function (viewport) {
  if (!viewport || viewport._dnSpyXdxHover) return;
  let highlighted = [];
  let current = null;
  let debugTarget = null;
  let debugTooltip = null;
  const clearHighlight = () => {
    for (const element of highlighted) element.classList.remove("code-link-active");
    highlighted = [];
    current = null;
  };
  const hideDebugTooltip = () => {
    debugTooltip?.remove();
    debugTooltip = null;
    debugTarget = null;
  };
  const clear = () => {
    clearHighlight();
    hideDebugTooltip();
  };
  const apply = symbol => {
    if (symbol === current) return;
    clearHighlight();
    if (!symbol) return;
    current = symbol;
    const selector = "[data-symbol=\"" + (window.CSS && CSS.escape ? CSS.escape(symbol) : symbol) + "\"]";
    highlighted = Array.from(viewport.querySelectorAll(selector));
    for (const element of highlighted) element.classList.add("code-link-active");
  };
  const showDebugTooltip = element => {
    if (element === debugTarget) return;
    hideDebugTooltip();
    if (!element) return;
    debugTarget = element;
    debugTooltip = document.createElement("div");
    debugTooltip.className = "debug-value-tooltip";
    debugTooltip.setAttribute("role", "tooltip");

    const name = document.createElement("strong");
    name.textContent = element.dataset.debugLocal || "";
    const value = document.createElement("code");
    value.textContent = element.dataset.debugValue || "";
    debugTooltip.append(name, value);
    if (element.dataset.debugType) {
      const type = document.createElement("small");
      type.textContent = element.dataset.debugType;
      debugTooltip.append(type);
    }
    document.body.append(debugTooltip);

    const target = element.getBoundingClientRect();
    const tooltip = debugTooltip.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - tooltip.width - 8, target.left));
    const below = target.bottom + 7;
    const top = below + tooltip.height <= window.innerHeight - 8
      ? below
      : Math.max(8, target.top - tooltip.height - 7);
    debugTooltip.style.left = left + "px";
    debugTooltip.style.top = top + "px";
  };
  const over = event => {
    const element = event.target?.closest?.("[data-symbol]");
    apply(element && viewport.contains(element) ? element.getAttribute("data-symbol") : null);
    const debugElement = event.target?.closest?.("[data-debug-local]");
    showDebugTooltip(debugElement && viewport.contains(debugElement) ? debugElement : null);
  };
  viewport.addEventListener("mouseover", over, { passive: true });
  viewport.addEventListener("mouseleave", clear, { passive: true });
  // Virtualize recycles line elements as they scroll out; drop any highlight so a class never lingers on a
  // node that has since been reused for a different line.
  viewport.addEventListener("scroll", clear, { passive: true });
  viewport._dnSpyXdxHover = { over, clear };
};
window.dnSpyXdx.disposeSourceHover = function (viewport) {
  if (!viewport?._dnSpyXdxHover) return;
  viewport.removeEventListener("mouseover", viewport._dnSpyXdxHover.over);
  viewport.removeEventListener("mouseleave", viewport._dnSpyXdxHover.clear);
  viewport.removeEventListener("scroll", viewport._dnSpyXdxHover.clear);
  delete viewport._dnSpyXdxHover;
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
// Driven by the native drag-drop handler (which took over WebView2's drop target, so the page no longer sees
// HTML drag events). Native fires this repeatedly with `on=true` while a file hovers; the timeout hides the
// overlay shortly after the hover stops, and `on=false` (a drop, or leaving) clears it at once.
window.dnSpyXdx.setDropOverlay = function (on) {
  const body = document.body;
  clearTimeout(window.dnSpyXdx.dropOverlayTimer);
  if (on) {
    body.classList.add("app-drag-over");
    window.dnSpyXdx.dropOverlayTimer = setTimeout(() => body.classList.remove("app-drag-over"), 400);
  } else {
    body.classList.remove("app-drag-over");
  }
};
window.dnSpyXdx.initFileDrop = function (dotNet) {
  if (window.dnSpyXdx.fileDropReady) return;
  window.dnSpyXdx.fileDropReady = true;
  const assembly = /\.(dll|exe|winmd)$/i;
  let depth = 0;
  const hasFiles = event => Array.from(event.dataTransfer?.types || []).includes("Files");
  const setOverlay = on => document.body.classList.toggle("app-drag-over", on);
  window.addEventListener("dragenter", event => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth++;
    setOverlay(true);
  });
  window.addEventListener("dragover", event => {
    if (!hasFiles(event)) return;
    // Stops the webview from navigating to the dropped file and allows the drop to land here instead.
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  });
  window.addEventListener("dragleave", event => {
    if (!hasFiles(event)) return;
    depth = Math.max(0, depth - 1);
    if (depth === 0) setOverlay(false);
  });
  window.addEventListener("drop", async event => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth = 0;
    setOverlay(false);
    const files = Array.from(event.dataTransfer.files).filter(file => assembly.test(file.name));
    if (files.length === 0) return;
    // Stage every dropped file into one folder first so sibling references resolve, then open them.
    const batch = (crypto.randomUUID && crypto.randomUUID()) || String(Date.now());
    const names = [];
    for (const file of files) {
      const bytes = new Uint8Array(await file.arrayBuffer());
      await dotNet.invokeMethodAsync("StageDroppedAssembly", batch, file.name, bytes);
      names.push(file.name);
    }
    await dotNet.invokeMethodAsync("OpenDroppedAssemblies", batch, names);
  });
};
window.dnSpyXdx.copyText = async function (text) {
  await navigator.clipboard.writeText(text);
};
