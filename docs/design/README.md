# CODING AGENTS: READ THIS FIRST

This is a **handoff bundle** from Claude Design (claude.ai/design).

A user mocked up designs in HTML/CSS/JS using an AI design tool, then exported this bundle so a coding agent can implement the designs for real.

## What you should do — IMPORTANT

**Find the primary design file under `docs/design/project/` and read it top to bottom.** `export-src.html` is the index and links all six boards. Then **follow its imports**: open every file it pulls in (shared components, CSS, scripts) so you understand how the pieces fit together before you start implementing.

**If anything is ambiguous, ask the user to confirm before you start implementing.** It's much cheaper to clarify scope up front than to build the wrong thing.

## About the design files

The design medium is **HTML/CSS/JS** — these are prototypes, not production code. Your job is to **recreate them pixel-perfectly** in whatever technology makes sense for the target codebase (React, Vue, native, whatever fits). Match the visual output; don't copy the prototype's internal structure unless it happens to fit.

**Don't screenshot these files to extract specs unless the user asks you to.** Everything you need — dimensions, colors, layout rules — is spelled out in the source. Read the HTML and CSS directly; a screenshot won't tell you anything they don't. Rendering the boards is for humans reviewing the design (see below).

## Viewing locally

```bash
node docs/design/serve.mjs        # → http://localhost:4300
```

Then open <http://localhost:4300> (or run the **serve-design** VS Code task). `--port 4301` moves it if 4300 is taken.

Three things to know:

- **The boards must be served over HTTP.** Opening a `.dc.html` file directly from disk fails: `support.js` resolves every `<dc-import name="Sidebar">` by `fetch()`-ing the sibling file, and a `file://` document is an opaque origin, so the browser blocks those fetches with a CORS error and every shared component renders as an empty placeholder.
- **`export-src.html` is the entry point** — the index linking boards 01 through 06. The server redirects `/` to it.
- **An internet connection is required.** React and ReactDOM load from unpkg, and Bootstrap 5.3, Bootstrap Icons, and the Montserrat/Inter/JetBrains Mono fonts load from their CDNs.

## Bundle contents

- `docs/design/README.md` — this file
- `docs/design/serve.mjs` — zero-dependency local static server for the bundle
- `docs/design/project/` — the `Chat and Streaming review` project files (HTML prototypes, assets, components)
