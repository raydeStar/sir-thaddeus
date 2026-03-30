# Privacy

Sir Thaddeus is built to run on **your machine**, under **your control**.

---

## What Sir Thaddeus collects

**Nothing.**

No telemetry.  
No analytics.  
No usage tracking.  
No crash reports sent to outside services.

Every action the agent takes is recorded locally in an audit log on your machine — not shipped anywhere.

---

## AI and model calls

Sir Thaddeus does **not** send your prompts to external AI providers.

Model calls go only to **your configured local model server** — by default, LM Studio at `http://localhost:1234`. If you configure a different endpoint, calls go there. Nothing is routed through Sir Thaddeus servers because there are no Sir Thaddeus servers.

---

## Web and browser tools

If you ask Sir Thaddeus to search the web, check a website, or browse a URL, it will make network requests to **those specific sites you requested** — and only those.

It does not make background web requests or send data to third-party servers on its own initiative.

---

## First-run model downloads

The voice backend downloads local ASR and TTS models (Whisper and Kokoro) on first launch. These downloads come from their respective public model sources and are required to run voice features locally. After that initial setup, voice processing stays on your machine.

---

## Accounts and cloud services

Sir Thaddeus does **not** require:

- accounts
- sign-ups
- subscriptions
- cloud sync

You do not need to create an account to use the product.

---

## Where your data stays

Your data stays on **your machine**.

Your prompts, local files, conversation history, and agent memory are not sent to outside services by Sir Thaddeus. Local audit logs are written to `%LOCALAPPDATA%\SirThaddeus\audit.jsonl` and stay there.

---

## In plain English

- Your AI runs on your machine, not ours
- Your prompts go to your local model server, not to the cloud
- Your data is not collected, tracked, or reported anywhere
- No account is needed to use this software

That is the point.

---

*For security vulnerability reporting, see [SECURITY.md](SECURITY.md).*
