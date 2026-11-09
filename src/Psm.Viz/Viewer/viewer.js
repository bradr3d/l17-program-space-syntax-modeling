// ==========================================================================
// Psm.Viz replay viewer
// Embedded into every replay .html by HtmlReplayWriter. Expects a global REPLAY
// object (written just before this script) shaped like:
//
//   REPLAY = {
//     view:     [minX, minY, width, height],  // SVG viewBox that fits every frame
//     css:      "...",                        // shape classes, for "Save frame .svg"
//     captions: ["Initial 0", ...],           // one per frame
//     frames:   ["<g data-layer=...>", ...]   // SVG markup per frame
//   }
//
// Debugging: in DevTools this file shows up as "psm-viewer.js" (see sourceURL at
// the bottom), and `viewer` is exposed on window, e.g. viewer.show(10).
// ==========================================================================

"use strict";

(function () {
    const ZOOM_STEP = 1.15;          // scroll-wheel zoom factor per notch
    const EXPORT_WIDTH_PX = 1400;    // width of "Save frame .svg" output

    // ---- DOM ---------------------------------------------------------------

    const el = {
        stage: document.getElementById("stage"),
        frame: document.getElementById("frame"),
        caption: document.getElementById("caption"),
        slider: document.getElementById("slider"),
        play: document.getElementById("play"),
        fps: document.getElementById("fps"),
        prev: document.getElementById("prev"),
        next: document.getElementById("next"),
        fit: document.getElementById("fit"),
        save: document.getElementById("save"),
        layerToggles: Array.from(document.querySelectorAll("input[data-layer]")),
    };

    // ---- state -------------------------------------------------------------

    const frameCount = REPLAY.frames.length;
    let current = frameCount - 1;        // start on the final layout
    let timer = null;                    // setInterval id while playing
    let viewBox = [...REPLAY.view];      // current camera; changes with zoom/pan
    let drag = null;                     // { x, y, startViewBox } while panning

    // ---- frames ------------------------------------------------------------

    function show(index) {
        current = Math.max(0, Math.min(frameCount - 1, index));
        el.frame.innerHTML = REPLAY.frames[current];
        el.slider.value = current;
        el.caption.textContent = `${REPLAY.captions[current]}   (frame ${current + 1}/${frameCount})`;
        applyLayerVisibility();
    }

    function applyLayerVisibility() {
        for (const toggle of el.layerToggles) {
            const groups = el.frame.querySelectorAll(`g[data-layer="${toggle.dataset.layer}"]`);
            for (const group of groups) {
                group.style.display = toggle.checked ? "" : "none";
            }
        }
    }

    // ---- playback ----------------------------------------------------------

    function isPlaying() {
        return timer !== null;
    }

    function play() {
        if (current >= frameCount - 1) {
            show(0);                     // restart from the beginning
        }
        el.play.textContent = "❚❚ Pause";
        timer = setInterval(() => {
            if (current >= frameCount - 1) {
                pause();
            } else {
                show(current + 1);
            }
        }, 1000 / Number(el.fps.value));
    }

    function pause() {
        clearInterval(timer);
        timer = null;
        el.play.textContent = "▶ Play";
    }

    function togglePlay() {
        if (isPlaying()) {
            pause();
        } else {
            play();
        }
    }

    // ---- camera: zoom + pan ------------------------------------------------

    function applyViewBox() {
        el.stage.setAttribute("viewBox", viewBox.join(" "));
    }

    function resetView() {
        viewBox = [...REPLAY.view];
        applyViewBox();
    }

    // Pixels-per-world-unit and letterbox offsets for the current viewBox
    // (the SVG uses preserveAspectRatio="xMidYMid meet").
    function screenMapping() {
        const rect = el.stage.getBoundingClientRect();
        const scale = Math.min(rect.width / viewBox[2], rect.height / viewBox[3]);
        const offsetX = (rect.width - viewBox[2] * scale) / 2;
        const offsetY = (rect.height - viewBox[3] * scale) / 2;
        return { rect, scale, offsetX, offsetY };
    }

    function onWheel(event) {
        event.preventDefault();
        const { rect, scale, offsetX, offsetY } = screenMapping();

        // World point under the cursor stays fixed while zooming.
        const worldX = viewBox[0] + (event.clientX - rect.left - offsetX) / scale;
        const worldY = viewBox[1] + (event.clientY - rect.top - offsetY) / scale;
        const k = event.deltaY > 0 ? ZOOM_STEP : 1 / ZOOM_STEP;

        viewBox = [
            worldX - (worldX - viewBox[0]) * k,
            worldY - (worldY - viewBox[1]) * k,
            viewBox[2] * k,
            viewBox[3] * k,
        ];
        applyViewBox();
    }

    function onPointerDown(event) {
        drag = { x: event.clientX, y: event.clientY, startViewBox: [...viewBox] };
        el.stage.setPointerCapture(event.pointerId);
    }

    function onPointerMove(event) {
        if (drag === null) {
            return;
        }
        const { scale } = screenMapping();
        viewBox[0] = drag.startViewBox[0] - (event.clientX - drag.x) / scale;
        viewBox[1] = drag.startViewBox[1] - (event.clientY - drag.y) / scale;
        applyViewBox();
    }

    function onPointerUp() {
        drag = null;
    }

    // ---- export ------------------------------------------------------------

    function saveCurrentFrameAsSvg() {
        const [x, y, w, h] = REPLAY.view;
        const heightPx = Math.round(EXPORT_WIDTH_PX * h / w);
        const svg =
            `<svg xmlns="http://www.w3.org/2000/svg" class="psm" viewBox="${x} ${y} ${w} ${h}" ` +
            `width="${EXPORT_WIDTH_PX}" height="${heightPx}">\n` +
            `<style>${REPLAY.css}</style>\n` +
            `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="#fff"/>\n` +
            REPLAY.frames[current] +
            `</svg>\n`;

        const link = document.createElement("a");
        link.href = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml" }));
        link.download = `frame-${String(current).padStart(4, "0")}.svg`;
        link.click();
        URL.revokeObjectURL(link.href);
    }

    // ---- keyboard ----------------------------------------------------------

    function onKeyDown(event) {
        switch (event.key) {
            case "ArrowRight":
                show(current + 1);
                break;
            case "ArrowLeft":
                show(current - 1);
                break;
            case " ":
                event.preventDefault();
                togglePlay();
                break;
        }
    }

    // ---- wire up -----------------------------------------------------------

    el.play.addEventListener("click", togglePlay);
    el.prev.addEventListener("click", () => show(current - 1));
    el.next.addEventListener("click", () => show(current + 1));
    el.fit.addEventListener("click", resetView);
    el.save.addEventListener("click", saveCurrentFrameAsSvg);
    el.slider.addEventListener("input", () => show(Number(el.slider.value)));
    el.fps.addEventListener("change", () => {
        if (isPlaying()) {
            pause();
            play();                      // restart the timer at the new speed
        }
    });
    for (const toggle of el.layerToggles) {
        toggle.addEventListener("change", applyLayerVisibility);
    }

    document.addEventListener("keydown", onKeyDown);
    el.stage.addEventListener("wheel", onWheel, { passive: false });
    el.stage.addEventListener("pointerdown", onPointerDown);
    el.stage.addEventListener("pointermove", onPointerMove);
    el.stage.addEventListener("pointerup", onPointerUp);

    // Handy from the DevTools console.
    window.viewer = { show, play, pause, resetView, get current() { return current; } };

    show(current);
})();
