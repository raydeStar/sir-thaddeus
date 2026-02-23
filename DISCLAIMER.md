# ⚠ IMPORTANT: DISCLAIMER & SETUP NOTICE

**Sir Thaddeus is currently in Early Access / Development.**

Please read the following instructions carefully before running the application to ensure the best experience and to avoid common setup issues.

## 1. Local LLM Required
Sir Thaddeus **requires** a local LLM server to function. By default, it is configured to connect to **LM Studio** at `http://localhost:1234`. You must start your LLM server *before* or *immediately after* launching the app.

## 2. Voice Backend Warmup
The voice engine (ASR/TTS) is self-contained but requires initialization on first run:
- **Automatic Downloads**: On first launch, the app may download several hundred MBs of AI models (Whisper and Kokoro).
- **Background Startup**: The main application window will open immediately, but voice features (Hold to Talk) will be **disabled** while the background services warm up.
- **Status Banner**: A status banner at the top of the chat window will show the current progress (e.g., "Downloading models...", "Starting voice services...").

## 3. Security & Privacy
- **Local-First**: All processing happens on your machine. No data is sent to the cloud by Sir Thaddeus itself.
- **Unsigned Binaries**: As an early-stage project, these binaries are currently unsigned. Your antivirus or Windows SmartScreen may flag the executable. Verify the source and use "Run anyway" at your own discretion.
- **Audit Logs**: Every action the agent takes is logged locally for your review at `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`.

## 4. Troubleshooting
If you experience issues:
1. Check the **Audit Log** for errors.
2. Ensure no other process is using ports `8001` or `17845`.
3. Refer to `README_FIRST_RUN.md` for more detailed configuration steps.

---
*By running Sir Thaddeus, you acknowledge that this is experimental software and carry out use at your own risk.*
