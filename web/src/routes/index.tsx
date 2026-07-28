import { createFileRoute, useNavigate, Link } from '@tanstack/react-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ChevronRight, Loader2, MessageSquare, Mic, Square, Unplug } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { useRuntimeStore } from '../stores/runtimeStore';
import { ChatComposer, type WikiContextSelection } from '../components/ChatComposer';
import { acquireMicStream, isStreamLive, prepareMicCapture, stopMicStream } from '../lib/micCapture';
import { trimSilenceToWav } from '../lib/audioTrim';
import { transcribeSpeech, warmVoiceHost } from '../lib/voiceApi';
import { ThaddeusSignet } from '../components/ThaddeusSignet';

export const Route = createFileRoute('/')({
  component: HomeRoute,
});

const MIN_VOICE_HOLD_MS = 350;

// Quiet capability cues. Selecting one drafts the request for review.
const STARTER_PROMPTS = [
  'Summarize what is on my screen',
  'Find something in my Wiki',
  'Plan a task with me',
];

function HomeRoute() {
  const navigate = useNavigate();
  const newThread = useChatStore((s) => s.newThread);
  const send = useChatStore((s) => s.send);
  const threads = useChatStore((s) => s.threads);
  const loadThreads = useChatStore((s) => s.loadThreads);
  const storeError = useChatStore((s) => s.error);

  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);

  // Lightweight PTT for the home screen. We keep this self-contained so it
  // does not pull in the chat route's full speech-playback machinery: hold
  // mic, record, release, transcribe, then start a new thread.
  const [voiceState, setVoiceState] = useState<'idle' | 'starting' | 'recording' | 'transcribing'>('idle');
  const recorderRef = useRef<MediaRecorder | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const warmStreamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const startedAtRef = useRef(0);
  const abortRef = useRef(false);

  useEffect(() => {
    return () => {
      // Best-effort teardown when leaving the home route mid-capture.
      if (recorderRef.current && recorderRef.current.state !== 'inactive') {
        try { recorderRef.current.stop(); } catch { /* ignore */ }
      }
      if (streamRef.current) {
        stopMicStream(streamRef.current);
        streamRef.current = null;
      }
      if (warmStreamRef.current) {
        stopMicStream(warmStreamRef.current);
        warmStreamRef.current = null;
      }
    };
  }, []);

  useEffect(() => {
    void loadThreads();
  }, [loadThreads]);

  useEffect(() => {
    void warmVoiceHost().catch(() => undefined);
    void prepareMicCapture().catch(() => undefined);
  }, []);

  const start = useCallback(async (text: string, wikiContext?: WikiContextSelection) => {
    if (busy) return;
    setBusy(true);
    setLocalError(null);
    try {
      const t = await newThread();
      void navigate({
        to: '/chat/$threadId',
        params: { threadId: t.id },
        search: { focusMessageId: undefined },
      });
      await useChatStore.getState().openThread(t.id);
      await send(text, wikiContext);
      setDraft('');
    } catch (e) {
      // Surface the failure so the user doesn't hit Send and see nothing
      // happen. Common causes: backend offline, auth token missing (when
      // opened directly in a browser), proxy misconfig.
      setLocalError((e as Error).message || 'Could not send your message.');
    } finally {
      setBusy(false);
    }
  }, [busy, navigate, newThread, send]);

  const beginVoiceCapture = useCallback(async () => {
    if (busy || voiceState !== 'idle') return;
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      setLocalError('Microphone capture is not available in this browser shell.');
      return;
    }

    setLocalError(null);
    chunksRef.current = [];
    abortRef.current = false;
    startedAtRef.current = performance.now();
    void warmVoiceHost().catch(() => undefined);
    const reusedWarm = isStreamLive(warmStreamRef.current);
    setVoiceState(reusedWarm ? 'recording' : 'starting');

    try {
      let stream: MediaStream;
      if (reusedWarm && warmStreamRef.current) {
        stream = warmStreamRef.current;
      } else {
        const acquired = await acquireMicStream();
        stream = acquired.stream;
        warmStreamRef.current = stream;
      }

      if (abortRef.current) {
        abortRef.current = false;
        setVoiceState('idle');
        return;
      }

      const recorder = new MediaRecorder(stream, mediaRecorderOptions());
      recorder.ondataavailable = (event) => {
        if (event.data.size > 0) chunksRef.current.push(event.data);
      };
      recorder.onerror = () => {
        setLocalError('Microphone recording failed.');
        releaseHomeMic(recorderRef, streamRef);
        setVoiceState('idle');
      };

      streamRef.current = stream;
      recorderRef.current = recorder;
      recorder.start();
      setVoiceState('recording');
    } catch (e) {
      releaseHomeMic(recorderRef, streamRef);
      setVoiceState('idle');
      if (!abortRef.current) {
        setLocalError((e as Error).message || 'Could not start microphone capture.');
      }
      abortRef.current = false;
    }
  }, [busy, voiceState]);

  const finishVoiceCapture = useCallback(async () => {
    const recorder = recorderRef.current;
    if (!recorder) {
      abortRef.current = true;
      return;
    }

    const heldMs = performance.now() - startedAtRef.current;
    const shortTap = heldMs < MIN_VOICE_HOLD_MS;

    let audioBlob: Blob;
    try {
      audioBlob = await stopRecorder(recorder, chunksRef.current);
    } catch (e) {
      releaseHomeMic(recorderRef, streamRef);
      setVoiceState('idle');
      if (!shortTap) setLocalError((e as Error).message || 'Could not finish microphone recording.');
      return;
    }

    releaseHomeMic(recorderRef, streamRef);
    if (shortTap) {
      setVoiceState('idle');
      return;
    }
    if (audioBlob.size === 0) {
      setVoiceState('idle');
      setLocalError('No microphone audio was captured.');
      return;
    }

    setVoiceState('transcribing');
    setLocalError(null);
    try {
      const trimmed = await trimSilenceToWav(audioBlob).catch(() => audioBlob);
      const transcript = await transcribeSpeech(trimmed);
      const text = transcript.text.trim();
      if (!text) {
        setLocalError('No speech was detected.');
        return;
      }
      await start(text);
    } catch (e) {
      setLocalError((e as Error).message || 'Could not transcribe the microphone audio.');
    } finally {
      setVoiceState('idle');
    }
  }, [start]);

  const connected = useRuntimeStore((s) => s.connected);
  // While disconnected the designed notice below owns the "runtime is not
  // reachable" story; piping the store's raw fetch error into a red alert
  // would say the same thing twice, the second time in debug-speak.
  const displayError = localError ?? (connected ? storeError : null);
  const recent = threads.slice(0, 6);

  return (
    <section
      data-testid="route-home"
      className="mx-auto flex min-h-full w-full max-w-[700px] flex-col px-5 pt-14 pb-16 sm:px-6 md:pt-20 lg:justify-center lg:pb-24 lg:pt-0"
    >
      {/* Hero mark — small, calm. Signals identity without being loud. */}
      <div className="mx-auto drop-shadow-[0_12px_28px_rgba(201,146,57,0.24)]">
        <ThaddeusSignet className="h-16 w-16" />
      </div>

      <p className="mt-5 text-center text-[10px] font-semibold uppercase tracking-[0.24em] text-accent">
        Sir Thaddeus
      </p>

      {/* Single-line headline. The app's strongest surface — one big sentence. */}
      <h1 className="mt-3 text-center text-[38px] font-semibold leading-[1.08] tracking-[-0.04em] text-ink sm:text-[42px]">
        How can I help?
      </h1>
      <p className="mt-3 text-center text-[15px] text-ink-muted">
        Your model, memory, and tools—always within your rules.
      </p>

      <div className="mt-9">
        <ChatComposer
          value={draft}
          onChange={setDraft}
          onSubmit={start}
          sending={busy}
          inputTestId="home-prompt"
          sendTestId="home-send"
          autoFocus
          rightActions={
            <button
              type="button"
              className={`chat-composer-icon-button ${voiceState === 'recording' ? 'border-red-400 bg-red-500/10 text-red-700 dark:text-red-300' : ''}`}
              aria-label={homeVoiceButtonLabel(voiceState)}
              title={homeVoiceButtonLabel(voiceState)}
              data-testid="home-voice-hold"
              disabled={busy}
              onPointerDown={(event) => {
                if (event.button !== 0) return;
                event.currentTarget.setPointerCapture(event.pointerId);
                void beginVoiceCapture();
              }}
              onPointerUp={(event) => {
                if (event.currentTarget.hasPointerCapture(event.pointerId)) {
                  event.currentTarget.releasePointerCapture(event.pointerId);
                }
                void finishVoiceCapture();
              }}
              onPointerCancel={() => { void finishVoiceCapture(); }}
            >
              {voiceState === 'starting' || voiceState === 'transcribing' ? (
                <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.9} />
              ) : voiceState === 'recording' ? (
                <Square className="h-4 w-4" strokeWidth={1.9} />
              ) : (
                <Mic className="h-4 w-4" strokeWidth={1.9} />
              )}
            </button>
          }
        />
        <p className="mt-3 text-center text-[11px] text-ink-subtle">
          Press <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> to send
          <span className="mx-2">·</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Shift</kbd>
          <span className="mx-1">+</span>
          <kbd className="rounded bg-canvas-sunken px-1.5 py-0.5 font-mono text-[10px]">Enter</kbd> for newline
        </p>
        {displayError ? (
          <div
            role="alert"
            data-testid="home-send-error"
            className="mt-3 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-[13px] text-red-700 dark:text-red-300"
          >
            {displayError}
          </div>
        ) : null}

        {!connected ? (
          <div
            data-testid="home-disconnected-notice"
            className="mt-6 flex items-start gap-3 rounded-xl border border-line bg-canvas-raised px-4 py-3.5"
          >
            <span
              className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-canvas-sunken text-ink-subtle"
              aria-hidden
            >
              <Unplug className="h-4 w-4" strokeWidth={1.75} />
            </span>
            <div className="min-w-0">
              <p className="text-[13.5px] font-medium text-ink">Waiting for the local runtime</p>
              <p className="mt-0.5 text-[12.5px] leading-relaxed text-ink-muted">
                Sir Thaddeus runs entirely on this machine. Start the desktop app (or its
                runtime) and this workspace will connect on the next refresh.
              </p>
            </div>
          </div>
        ) : null}

        {connected ? (
          <div className="mt-6 flex flex-wrap justify-center gap-2" data-testid="home-starter-prompts">
            {STARTER_PROMPTS.map((prompt) => (
              <button
                key={prompt}
                type="button"
                disabled={busy}
                onClick={() => setDraft(prompt)}
                className="rounded-full border border-line bg-canvas-raised px-3.5 py-2 text-[13px] text-ink-muted transition-colors hover:border-accent/60 hover:text-accent disabled:opacity-50"
              >
                {prompt}
              </button>
            ))}
          </div>
        ) : null}
      </div>

      {/* Recents. Only renders when there are threads — otherwise the hero breathes. */}
      {recent.length > 0 ? (
        <nav aria-label="Recent conversations" className="mt-16 lg:hidden">
          {/* Hairline divider gives the section its own visual weight so it
              doesn't read as a continuation of the input hint. */}
          <div className="mb-6 h-px bg-line" aria-hidden />
          <div className="mb-4 flex items-baseline justify-between">
            <p className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle">
              Recent
            </p>
            <Link
              to="/chat"
              className="text-[11px] font-medium uppercase tracking-[0.08em] text-ink-subtle transition-colors hover:text-accent"
            >
              View all
            </Link>
          </div>
          <ul className="space-y-1">
            {recent.map((t) => (
              <li key={t.id}>
                <Link
                  to="/chat/$threadId"
                  params={{ threadId: t.id }}
                  search={{ focusMessageId: undefined }}
                  data-testid={`home-recent-${t.id}`}
                  className="group/recent flex items-center gap-3 rounded-xl border border-transparent px-3 py-2.5 text-sm text-ink transition-all hover:border-line hover:bg-canvas-raised hover:shadow-soft"
                >
                  <span
                    className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-canvas-sunken text-ink-subtle transition-colors group-hover/recent:bg-accent-soft group-hover/recent:text-accent"
                    aria-hidden
                  >
                    <MessageSquare className="h-3.5 w-3.5" strokeWidth={1.75} />
                  </span>
                  <span className="min-w-0 flex-1 truncate">
                    {t.title || 'Untitled conversation'}
                  </span>
                  <span className="shrink-0 text-xs tabular-nums text-ink-subtle">
                    {formatRelative(t.updatedAt)}
                  </span>
                  <ChevronRight
                    className="h-3.5 w-3.5 shrink-0 text-ink-subtle opacity-0 transition-opacity group-hover/recent:opacity-100"
                    strokeWidth={1.75}
                    aria-hidden
                  />
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      ) : null}
    </section>
  );
}

function formatRelative(iso: string): string {
  try {
    const then = new Date(iso).getTime();
    const now = Date.now();
    const mins = Math.round((now - then) / 60_000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.round(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    const days = Math.round(hrs / 24);
    if (days < 7) return `${days}d ago`;
    return new Date(iso).toLocaleDateString();
  } catch {
    return '';
  }
}

function mediaRecorderOptions(): MediaRecorderOptions {
  for (const mimeType of ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus']) {
    if (MediaRecorder.isTypeSupported(mimeType)) return { mimeType };
  }
  return {};
}

function stopRecorder(recorder: MediaRecorder, chunks: Blob[]): Promise<Blob> {
  return new Promise((resolve, reject) => {
    const mimeType = recorder.mimeType || 'audio/webm';
    recorder.onstop = () => resolve(new Blob(chunks, { type: mimeType }));
    recorder.onerror = () => reject(new Error('Microphone recording failed.'));
    if (recorder.state === 'inactive') {
      resolve(new Blob(chunks, { type: mimeType }));
      return;
    }
    recorder.stop();
  });
}

function releaseHomeMic(
  recorderRef: React.MutableRefObject<MediaRecorder | null>,
  streamRef: React.MutableRefObject<MediaStream | null>,
): void {
  const recorder = recorderRef.current;
  if (recorder && recorder.state !== 'inactive') {
    try { recorder.stop(); } catch { /* best effort */ }
  }
  recorderRef.current = null;
  // The home route keeps the first successful MediaStream warm so repeat
  // PTT presses don't pay getUserMedia startup again. Unmount owns teardown.
  streamRef.current = null;
}

function homeVoiceButtonLabel(state: 'idle' | 'starting' | 'recording' | 'transcribing'): string {
  if (state === 'starting') return 'Starting microphone';
  if (state === 'recording') return 'Release to send voice message';
  if (state === 'transcribing') return 'Transcribing voice message';
  return 'Hold to talk';
}
