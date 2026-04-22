# wwwroot

This directory is the static-asset root for the workspace SPA. The Vite build of
`web/` outputs into this folder via `scripts/build` (Phase 1.5 wires that up).

During development this directory is empty; the runtime serves a development
placeholder page when no `index.html` is present (see `WorkspaceHostingExtensions`).
