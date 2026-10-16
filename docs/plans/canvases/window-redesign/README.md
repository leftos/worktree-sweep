# Worktree Sweep window redesign

## Viewing

Open `index.html` for every artboard laid out as on the canvas, or any `project/*.dc.html` on its own. `project/support.js` is the Design type's runtime, put there by `~/.claude/tools/canvas-offline`.

An artboard that mounts a child with `<dc-import>` renders it only when the folder is served: `python -m http.server -d <this folder>`, then open http://localhost:8000/.
