# thaddeus-web

Sir Thaddeus web workspace. React 18 + Vite 8 + the local router compatibility
shim + Zustand + Tailwind 3.

## Scripts

```bash
npm install         # one-time
npm run dev         # vite dev on http://127.0.0.1:5173
npm run typecheck   # tsc -b without emit
npm run build       # produces ./dist (copied to src/Thaddeus.Runtime/wwwroot in Phase 1.5)
```

## Runtime metadata

The runtime injects the bearer token, port, version, and route hint into `<meta>`
tags before serving `index.html`. `src/lib/runtime.ts` reads them; `src/stores/runtimeStore.ts`
opens the WebSocket against `/ws?access_token=…`.

When run under `npm run dev` (no runtime in the loop), the store simply does not
connect — the UI still renders so component work can proceed offline.

## Routes

Current route files:

| Route | File |
|-------|------|
| `/` | `routes/index.tsx` |
| `/onboarding` | `routes/onboarding.tsx` |
| `/chat`, `/chat/:threadId` | `routes/chat.tsx`, `routes/chat.$threadId.tsx` |
| `/history` | `routes/history.tsx` |
| `/activity`, `/activity/:entryId` | `routes/activity.tsx`, `routes/activity.$entryId.tsx` |
| `/memory` | `routes/memory.tsx` |
| `/wiki` | `routes/wiki.tsx` |
| `/routines`, `/routines/:id/edit`, `/routines/:id/history`, `/routines/:id/run` | `routes/routines.tsx`, `routes/routines.$id.*.tsx` |
| `/settings`, `/settings/:category` | `routes/settings.tsx`, `routes/settings.$category.tsx` |
| `/diagnostics` | `routes/diagnostics.tsx` |
| `/compact` | `routes/compact.tsx` (quick-interaction panel) |

`src/routeTree.ts` is a tracked static route tree. Imports from
`@tanstack/react-router` are aliased to `src/lib/routerShim.tsx` in TypeScript
and Vite config.
