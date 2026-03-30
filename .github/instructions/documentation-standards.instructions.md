---
name: 'Documentation Writing Standards'
description: 'Style and accuracy rules that apply whenever any agent edits user-facing documentation in the Sir Thaddeus repository.'
applyTo: 'README*.md,CHANGELOG.md,CONTRIBUTING.md,SECURITY.md,DISCLAIMER.md,docs/**'
---

# Documentation Writing Standards

These rules apply to **every agent** editing user-facing documentation in this repository —
not just the readme-specialist. They ensure a consistent voice, accurate claims, and a
trustworthy first impression.

## Voice and Tone

- Write clearly and simply. Prefer short paragraphs.
- Use a calm, confident tone — not breathless, not timid.
- Favor concrete language over abstractions.
- Avoid hype-heavy startup language and pitch-deck phrasing.

## Accuracy

- Match the repo's actual capabilities. Do not claim unverified features.
- Preserve accurate links, commands, filenames, and doc references.
- If a claim cannot be verified from the codebase, soften the wording or flag it.

## Platform Claims

- Verified support → "runs on."
- Intended but untested → "designed for" or "built for."
- Single-platform today → state that plainly.

## Structure

- Keep the top of any README sparse and scannable.
- Explain what the product is in plain language before architecture.
- Put advanced technical material below the fold (use `<details>` where appropriate).
- Quick Start sections: 5 steps max, practical, no preamble.

## Privacy and Trust Language

Sir Thaddeus is a local-first AI copilot. Its positioning centers on user control,
explicit permissions, visible actions, and privacy. When editing docs:

- Make privacy and control easy to understand.
- Do not overstate security or privacy guarantees.
- Prefer honest, specific language over impressive-sounding generalities.

## Priority Order

When multiple improvements compete, choose in this order:

1. Accuracy
2. Clarity
3. First-time user comprehension
4. Trustworthiness
5. Brevity
6. Elegance
