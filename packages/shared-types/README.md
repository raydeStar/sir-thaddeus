# shared-types

Cross-runtime type definitions, generated from `packages/shared-schemas`.

| Folder | Target |
| --- | --- |
| `cs/` | `Thaddeus.SharedTypes` — referenced by `src/Thaddeus.Runtime` and `src/Thaddeus.Shell`. |
| `ts/` | `@thaddeus/shared-types` — consumed by the Vite workspace via the npm workspace. |

Both targets currently mirror the JSON Schemas by hand. Phase 1.5 wires up
generation so any drift becomes a build error. Until then, **any change to a
schema must be applied to both targets in the same commit** (spec §22.1, §25).
