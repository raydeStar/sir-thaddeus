import { createFileRoute, Link } from '@tanstack/react-router';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowLeft, Check, Clipboard, EyeOff, Loader2, Mic, Plus, RotateCcw, Send, Square, Volume2, WifiOff, X } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { Markdown } from '../components/Markdown';
import { SourceCards } from '../components/SourceCards';
import { ChatComposer, type WikiContextSelection } from '../components/ChatComposer';
import { PermissionPauseCard } from '../components/PermissionModal';
import { SteerableProgressCard } from '../components/SteerableProgressCard';
import { WorkReceipt } from '../components/WorkReceipt';
import { PlanApprovalCard } from '../components/PlanApprovalCard';
import { subscribeVoicePttEvents, synthesizeSpeech, transcribeSpeech, warmVoiceHost } from '../lib/voiceApi';
import { stopAllProcesses } from '../lib/runtimeActions';
import { getSettings, putSettings } from '../lib/settingsApi';
import { acquireMicStream, isStreamLive, prepareMicCapture, stopMicStream } from '../lib/micCapture';
import { trimSilenceToWav } from '../lib/audioTrim';
import type { ChatMessageSource } from '@thaddeus/shared-types';
import { usePermissionsStore } from '../stores/permissionsStore';

const MIN_VOICE_HOLD_MS = 350;

export const Route = createFileRoute('/chat/$threadId')({
  validateSearch: (search: Record<string, unknown>) => ({
    focusMessageId: typeof search.focusMessageId === 'string' ? search.focusMessageId : undefined,
  }),
  component: ChatThreadRoute,
});

function ChatThreadRoute() {
  const { threadId } = Route.useParams();
  const { focusMessageId } = Route.useSearch();
  const thread = useChatStore((s) => s.activeThread);
  const activeTurn = useChatStore((s) => s.activeTurn);
  const activeRun = useChatStore((s) => s.activeRun);
  const sending = useChatStore((s) => s.sending);
  const error = useChatStore((s) => s.error);
  const openThread = useChatStore((s) => s.openThread);
  const send = useChatStore((s) => s.send);
  const retryLatestResponse = useChatStore((s) => s.retryLatestResponse);
  const pauseActiveRun = useChatStore((s) => s.pauseActiveRun);
  const resumeActiveRun = useChatStore((s) => s.resumeActiveRun);
  const takeOverActiveRun = useChatStore((s) => s.takeOverActiveRun);
  const redirectActiveRun = useChatStore((s) => s.redirectActiveRun);
  const cancelActiveRun = useChatStore((s) => s.cancelActiveRun);
  const approveActivePlan = useChatStore((s) => s.approveActivePlan);
  const editActivePlan = useChatStore((s) => s.editActivePlan);
  const permissionQueue = usePermissionsStore((s) => s.queue);

  const [draft, setDraft] = useState('');
  const [voiceTranscript, setVoiceTranscript] = useState<string | null>(null);
  const [speechError, setSpeechError] = useState<string | null>(null);
  const [speechState, setSpeechState] = useState<{ messageId: string; status: 'loading' | 'playing' } | null>(null);
  const [voiceState, setVoiceState] = useState<'idle' | 'starting' | 'recording' | 'transcribing' | 'sending'>('idle');
  const [offlineMode, setOfflineMode] = useState(false);
  const [offlineModeLoading, setOfflineModeLoading] = useState(true);
  const [offlineModeSaving, setOfflineModeSaving] = useState(false);
  const [ephemeralMemory, setEphemeralMemory] = useState(false);
  const [highlightedMessageId, setHighlightedMessageId] = useState<string | null>(null);
  const [steeringMode, setSteeringMode] = useState<'redirect' | 'takeover' | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const audioUrlRef = useRef<string | null>(null);
  const speechRequestRef = useRef(0);
  const recorderRef = useRef<MediaRecorder | null>(null);
  const micStreamRef = useRef<MediaStream | null>(null);
  const micChunksRef = useRef<Blob[]>([]);
  const micStartedAtRef = useRef(0);
  const abortPendingCaptureRef = useRef(false);
  const pendingVoiceResponseRef = useRef(false);
  const turnStartedAtRef = useRef<number | null>(null);
  // Persistent stream kept warm across PTT presses so the second and
  // subsequent presses skip the getUserMedia spinner.
  const warmStreamRef = useRef<MediaStream | null>(null);
  const voiceWarmupRef = useRef<Promise<unknown> | null>(null);

  const ensureVoiceWarmup = useCallback(() => {
    voiceWarmupRef.current ??= warmVoiceHost().catch(() => undefined);
    return voiceWarmupRef.current;
  }, []);

  const blockedByPermission = permissionQueue.some((request) => request.threadId === threadId);
  const blockedByPlan = Boolean(
    activeRun?.plan &&
    (activeRun.state === 'awaitingapproval' || activeRun.state === 'awaiting_approval'),
  );

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    if (activeTurn && turnStartedAtRef.current === null) {
      turnStartedAtRef.current = Date.now();
    } else if (!activeTurn) {
      turnStartedAtRef.current = null;
    }
  }, [activeTurn]);

  useEffect(() => {
    let disposed = false;
    setOfflineModeLoading(true);
    getSettings()
      .then((doc) => {
        if (!disposed) setOfflineMode(doc.privacy.offlineMode ?? false);
      })
      .catch(() => {
        if (!disposed) setOfflineMode(false);
      })
      .finally(() => {
        if (!disposed) setOfflineModeLoading(false);
      });
    return () => {
      disposed = true;
    };
  }, []);

  useEffect(() => {
    void ensureVoiceWarmup();
    void prepareMicCapture().catch(() => undefined);
  }, [ensureVoiceWarmup]);

  useEffect(() => {
    const container = scrollRef.current;
    if (!container) return;

    // Smart auto-scroll: prefer to bottom-pin so the newest text sits at
    // the bottom of the viewport, but if the latest assistant message
    // has grown tall enough that bottom-pinning would push its top
    // off-screen, anchor the top of the message at the top of the
    // viewport instead. The reader stays oriented at the *beginning*
    // of long responses rather than chasing the tail.
    const target = locateLatestMessageEl(container);
    if (!target) {
      container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
      return;
    }

    const TOP_PADDING = 24;
    const maxScroll = Math.max(0, container.scrollHeight - container.clientHeight);
    const containerRect = container.getBoundingClientRect();
    const targetRect = target.getBoundingClientRect();
    const targetTopInScrollCoords =
      targetRect.top - containerRect.top + container.scrollTop;
    const visibleTopAfterBottomPin = targetTopInScrollCoords - maxScroll;

    const nextScrollTop =
      visibleTopAfterBottomPin >= TOP_PADDING
        ? maxScroll
        : Math.max(0, Math.min(targetTopInScrollCoords - TOP_PADDING, maxScroll));

    container.scrollTo({ top: nextScrollTop, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

  useEffect(() => () => {
    speechRequestRef.current += 1;
    releaseSpeechAudio(audioRef, audioUrlRef);
    releaseMicCapture(recorderRef, micStreamRef);
    if (warmStreamRef.current) {
      stopMicStream(warmStreamRef.current);
      warmStreamRef.current = null;
    }
  }, []);

  const onSubmit = async (text: string, wikiContext?: WikiContextSelection) => {
    if (sending || blockedByPermission || blockedByPlan) return;
    if (steeringMode && activeRun) {
      await redirectActiveRun(text);
      setSteeringMode(null);
      setDraft('');
      return;
    }
    if (voiceTranscript !== null) {
      pendingVoiceResponseRef.current = true;
    }
    setVoiceTranscript(null);
    setDraft('');
    await send(text, wikiContext, { ephemeralMemory });
  };

  const toggleOfflineMode = useCallback(async () => {
    if (offlineModeSaving) return;
    const next = !offlineMode;
    setOfflineMode(next);
    setOfflineModeSaving(true);
    setSpeechError(null);
    try {
      const doc = await getSettings();
      const saved = await putSettings({
        ...doc,
        privacy: {
          ...doc.privacy,
          offlineMode: next,
        },
      });
      setOfflineMode(saved.privacy.offlineMode ?? false);
    } catch (e) {
      setOfflineMode(!next);
      setSpeechError((e as Error).message || 'Could not update offline mode.');
    } finally {
      setOfflineModeSaving(false);
      setOfflineModeLoading(false);
    }
  }, [offlineMode, offlineModeSaving]);

  const stopSpeech = useCallback(() => {
    speechRequestRef.current += 1;
    releaseSpeechAudio(audioRef, audioUrlRef);
    setSpeechState(null);
  }, []);

  const onSpeakMessage = useCallback(async (messageId: string, text: string) => {
    if (speechState?.messageId === messageId) {
      stopSpeech();
      return;
    }

    const requestId = speechRequestRef.current + 1;
    speechRequestRef.current = requestId;
    releaseSpeechAudio(audioRef, audioUrlRef);
    setSpeechError(null);
    setSpeechState({ messageId, status: 'loading' });

    // Split the reply into sentence-sized chunks so we can pipeline:
    // synthesize chunk N+1 while chunk N is playing. First audio out the
    // door is governed by the latency of the SHORTEST chunk, not the full
    // reply, so the user hears speech almost immediately on long answers.
    const chunks = chunkSpeechText(text);
    const cancelled = () => speechRequestRef.current !== requestId;

    try {
      // Kick off the first synthesis right away.
      let nextSynthPromise: Promise<Blob> = synthesizeSpeech(chunks[0]);
      let firstAudioStarted = false;

      for (let i = 0; i < chunks.length; i++) {
        const blob = await nextSynthPromise;
        if (cancelled()) return;

        // Pre-fetch the following chunk in parallel with playback.
        nextSynthPromise = i + 1 < chunks.length
          ? synthesizeSpeech(chunks[i + 1]).catch((err) => {
              throw err;
            })
          : Promise.resolve(new Blob());

        const url = URL.createObjectURL(blob);
        audioUrlRef.current = url;
        const audio = new Audio(url);
        audioRef.current = audio;

        if (!firstAudioStarted) {
          firstAudioStarted = true;
          setSpeechState({ messageId, status: 'playing' });
        }

        await new Promise<void>((resolve, reject) => {
          audio.addEventListener('ended', () => resolve(), { once: true });
          audio.addEventListener('error', () => reject(new Error('Could not play the synthesized audio.')), { once: true });
          audio.play().catch(reject);
        });

        if (cancelled()) return;
        URL.revokeObjectURL(url);
        if (audioUrlRef.current === url) audioUrlRef.current = null;
        if (audioRef.current === audio) audioRef.current = null;
      }

      if (!cancelled()) stopSpeech();
    } catch (e) {
      if (!cancelled()) {
        stopSpeech();
        setSpeechError((e as Error).message || 'Could not read that response aloud.');
      }
    }
  }, [speechState?.messageId, stopSpeech]);

  const triggerShutup = useCallback(async () => {
    stopSpeech();
    setSteeringMode(null);
    try {
      await stopAllProcesses();
    } catch {
      // Local playback is the important part for voice UX; runtime stop-all is best effort.
    }
  }, [stopSpeech]);

  const focusComposer = useCallback(() => {
    window.requestAnimationFrame(() => {
      document.querySelector<HTMLTextAreaElement>('[data-testid="chat-input"]')?.focus();
    });
  }, []);

  const redirectActiveWork = useCallback(async () => {
    await pauseActiveRun();
    setSteeringMode('redirect');
    setDraft((current) => current || '');
    focusComposer();
  }, [focusComposer, pauseActiveRun]);

  const takeOverActiveWork = useCallback(async () => {
    await takeOverActiveRun();
    setSteeringMode('takeover');
    setDraft('');
    focusComposer();
  }, [focusComposer, takeOverActiveRun]);

  const pauseOrResumeActiveWork = useCallback(async () => {
    if (activeRun?.state === 'paused' || activeRun?.state === 'pausing') {
      await resumeActiveRun();
    } else {
      await pauseActiveRun();
    }
  }, [activeRun?.state, pauseActiveRun, resumeActiveRun]);

  useEffect(() => {
    if (!activeTurn) return;
    const handler = (event: KeyboardEvent) => {
      if ((event.key !== '.' && event.key !== ' ') || event.altKey || event.ctrlKey || event.metaKey) return;
      const target = event.target as HTMLElement | null;
      if (target?.matches('input, textarea, button, [contenteditable="true"]')) return;
      event.preventDefault();
      if (event.key === '.') {
        void triggerShutup();
      } else {
        void pauseOrResumeActiveWork();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [activeTurn, pauseOrResumeActiveWork, triggerShutup]);

  const beginVoiceCapture = useCallback(async () => {
    if (recorderRef.current || voiceState !== 'idle') {
      await triggerShutup();
      return;
    }

    if (sending || activeTurn) {
      await triggerShutup();
      return;
    }

    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      setSpeechError('Microphone capture is not available in this browser shell.');
      return;
    }

    abortPendingCaptureRef.current = false;
    micChunksRef.current = [];
    micStartedAtRef.current = performance.now();
    void ensureVoiceWarmup();
    stopSpeech();
    setSpeechError(null);

    // Reuse a warm stream when possible so the user can speak instantly
    // on repeat presses. Only show the spinner when we genuinely need
    // to acquire a new stream from the OS.
    const reusedWarm = isStreamLive(warmStreamRef.current);
    if (!reusedWarm) {
      setVoiceState('starting');
    } else {
      setVoiceState('recording');
    }

    let stream: MediaStream;
    try {
      if (reusedWarm && warmStreamRef.current) {
        stream = warmStreamRef.current;
      } else {
        const acquired = await acquireMicStream();
        stream = acquired.stream;
        warmStreamRef.current = stream;
        if (acquired.usedDefault && acquired.requestedName) {
          // Surface a hint in the console so we can diagnose mismatches
          // without spamming a toast for every capture.
          console.warn(
            `[mic] Selected device "${acquired.requestedName}" not found in browser; using default "${acquired.resolvedLabel ?? 'unknown'}".`,
          );
        }
      }

      if (abortPendingCaptureRef.current) {
        abortPendingCaptureRef.current = false;
        setVoiceState('idle');
        return;
      }

      const recorder = new MediaRecorder(stream, mediaRecorderOptions());
      recorder.ondataavailable = (event) => {
        if (event.data.size > 0) micChunksRef.current.push(event.data);
      };
      recorder.onerror = () => {
        setSpeechError('Microphone recording failed.');
        releaseMicCapture(recorderRef, micStreamRef);
        setVoiceState('idle');
      };
      micStreamRef.current = stream;
      recorderRef.current = recorder;
      recorder.start();
      setVoiceState('recording');
    } catch (e) {
      releaseMicCapture(recorderRef, micStreamRef);
      // The warm stream may have died (device unplugged) — drop it so
      // the next attempt re-acquires.
      if (warmStreamRef.current && !isStreamLive(warmStreamRef.current)) {
        stopMicStream(warmStreamRef.current);
        warmStreamRef.current = null;
      }
      setVoiceState('idle');
      if (!abortPendingCaptureRef.current) {
        setSpeechError((e as Error).message || 'Could not start microphone capture.');
      }
      abortPendingCaptureRef.current = false;
    }
  }, [activeTurn, ensureVoiceWarmup, sending, stopSpeech, triggerShutup, voiceState]);

  const finishVoiceCapture = useCallback(async () => {
    const recorder = recorderRef.current;
    if (!recorder) {
      abortPendingCaptureRef.current = true;
      await triggerShutup();
      return;
    }

    const heldMs = performance.now() - micStartedAtRef.current;
    const shortTap = heldMs < MIN_VOICE_HOLD_MS;

    let audioBlob: Blob;
    try {
      audioBlob = await stopRecorder(recorder, micChunksRef.current);
    } catch (e) {
      releaseMicCapture(recorderRef, micStreamRef);
      setVoiceState('idle');
      if (!shortTap) setSpeechError((e as Error).message || 'Could not finish microphone recording.');
      return;
    }

    releaseMicCapture(recorderRef, micStreamRef);

    if (shortTap) {
      setVoiceState('idle');
      await triggerShutup();
      return;
    }

    if (audioBlob.size === 0) {
      setVoiceState('idle');
      setSpeechError('No microphone audio was captured.');
      return;
    }

    setVoiceState('transcribing');
    setSpeechError(null);
    try {
      // Strip leading/trailing silence in the browser before upload so
      // Whisper isn't paid to chew on the dead air around "uhh". Falls back
      // to the original blob on any decode failure.
      const trimmed = await trimSilenceToWav(audioBlob).catch(() => audioBlob);
      const transcript = await transcribeSpeech(trimmed, threadId);
      const text = transcript.text.trim();
      if (!text) {
        setVoiceState('idle');
        setSpeechError('No speech was detected.');
        return;
      }

      // Put the transcript in the same composer the keyboard path uses. The
      // user sees exactly what ASR heard and can correct it before any agent
      // action begins.
      setDraft(text);
      setVoiceTranscript(text);
    } catch (e) {
      setSpeechError((e as Error).message || 'Could not transcribe the microphone audio.');
    } finally {
      setVoiceState('idle');
    }
  }, [threadId, triggerShutup]);

  const pttHandlersRef = useRef({
    begin: () => undefined as void,
    finish: () => undefined as void,
    shutup: () => undefined as void,
  });

  useEffect(() => {
    pttHandlersRef.current = {
      begin: () => { void beginVoiceCapture(); },
      finish: () => { void finishVoiceCapture(); },
      shutup: () => { void triggerShutup(); },
    };
  }, [beginVoiceCapture, finishVoiceCapture, triggerShutup]);

  useEffect(() => {
    const controller = new AbortController();
    let disposed = false;
    const run = async () => {
      while (!disposed) {
        try {
          await subscribeVoicePttEvents((evt) => {
            if (evt.phase === 'down') pttHandlersRef.current.begin();
            else if (evt.phase === 'up') pttHandlersRef.current.finish();
            else pttHandlersRef.current.shutup();
          }, controller.signal);
        } catch {
          if (controller.signal.aborted) break;
        }
        if (!disposed) await new Promise((resolve) => window.setTimeout(resolve, 2000));
      }
    };
    void run();
    return () => {
      disposed = true;
      controller.abort();
    };
  }, []);

  // Memoize the message array so the speak-on-voice-reply effect below
  // doesn't re-fire every render when `thread?.messages` returns a fresh
  // `[]` literal (the `?? []` fallback created a new array each call).
  const messages = useMemo(() => thread?.messages ?? [], [thread?.messages]);
  const latestMessage = messages[messages.length - 1];
  const latestAssistantResponseId =
    !activeTurn && String(latestMessage?.role || '').toLowerCase() === 'assistant' && latestMessage?.text?.trim()
      ? latestMessage.id
      : null;
  const empty = messages.length === 0 && !activeTurn;

  useEffect(() => {
    if (!focusMessageId) return;

    const container = scrollRef.current;
    if (!container) return;

    const target = container.querySelector<HTMLElement>(`[data-testid="chat-message-${focusMessageId}"]`);
    if (!target) return;

    setHighlightedMessageId(focusMessageId);
    target.scrollIntoView({ behavior: 'smooth', block: 'center' });

    const timeout = window.setTimeout(() => {
      setHighlightedMessageId((current) => (current === focusMessageId ? null : current));
    }, 2600);

    return () => window.clearTimeout(timeout);
  }, [focusMessageId, messages.length, activeTurn?.messageId]);

  useEffect(() => {
    if (!pendingVoiceResponseRef.current || activeTurn) return;
    const latest = messages[messages.length - 1];
    if (String(latest?.role || '').toLowerCase() !== 'assistant' || !latest?.text?.trim()) return;
    pendingVoiceResponseRef.current = false;
    void onSpeakMessage(latest.id, latest.text);
  }, [activeTurn, messages, onSpeakMessage]);

  useEffect(() => {
    if (error) pendingVoiceResponseRef.current = false;
  }, [error]);

  return (
    <section
      data-testid="route-chat-thread"
      className="flex h-full flex-col"
    >
      {/* Ultra-thin header. The thread title is the content, not a chrome label. */}
      <div className="px-4 py-3 md:px-10">
        <div className="mx-auto flex w-full max-w-[720px] items-center gap-3">
          <Link
            to="/chat"
            className="flex h-7 w-7 items-center justify-center rounded-full text-ink-subtle transition-colors hover:text-ink"
            aria-label="Back to chats"
          >
            <ArrowLeft className="h-4 w-4" strokeWidth={1.75} />
          </Link>
          <h1 className="truncate text-[13px] font-medium text-ink-muted">
            {thread?.title ?? 'Loading…'}
          </h1>
        </div>
      </div>

      <div
        ref={scrollRef}
        data-testid="chat-message-list"
        role="log"
        aria-live="polite"
        aria-relevant="additions"
        aria-busy={Boolean(activeTurn)}
        className="flex-1 overflow-y-auto px-4 md:px-10"
      >
        <div className="mx-auto w-full max-w-[720px] py-6 pb-40">
          {empty ? (
            <div className="flex h-full items-center justify-center pt-24 text-center">
              <p className="text-sm text-ink-subtle" data-testid="chat-thread-empty">
                No messages yet. Say hello.
              </p>
            </div>
          ) : (
            <div className="space-y-8">
              {messages.map((m) => {
                const role = String(m.role || '').toLowerCase();
                if (role !== 'user' && !m.text?.trim()) return null;
                return (
                  <MessageRow
                    key={m.id}
                    role={role as MessageRowProps['role']}
                    text={m.text}
                    sources={m.sources ?? null}
                    messageId={m.id}
                    threadId={threadId}
                    isLatestAssistantResponse={m.id === latestAssistantResponseId}
                    onRetryLatest={() => void retryLatestResponse({ ephemeralMemory })}
                    retryDisabled={sending || Boolean(activeTurn)}
                    speechStatus={speechState?.messageId === m.id ? speechState.status : null}
                    onSpeak={() => void onSpeakMessage(m.id, m.text)}
                    highlighted={m.id === highlightedMessageId}
                    testId={`chat-message-${m.id}`}
                  />
                );
              })}
              {activeTurn ? (
                <MessageRow
                  role="assistant"
                  text={activeTurn.text || ''}
                  messageId={activeTurn.messageId}
                  threadId={threadId}
                  streaming
                  startedAt={turnStartedAtRef.current ?? undefined}
                  runState={activeRun?.state}
                  checkpoint={activeRun?.checkpoint}
                  plan={activeRun?.plan}
                  onPauseResume={() => { void pauseOrResumeActiveWork(); }}
                  onRedirect={redirectActiveWork}
                  onTakeOver={takeOverActiveWork}
                  onStop={() => { void triggerShutup(); }}
                  testId="chat-message-streaming"
                />
              ) : null}
              {blockedByPlan && activeRun?.plan ? (
                <PlanApprovalCard
                  plan={activeRun.plan}
                  onSave={editActivePlan}
                  onApprove={approveActivePlan}
                  onCancel={cancelActiveRun}
                />
              ) : null}
              <PermissionPauseCard threadId={threadId} />
            </div>
          )}
        </div>
      </div>

      {/* Composer. Single rounded shape floating above a subtle top gradient. */}
      <div className="relative px-4 pb-6 pt-2 md:px-10">
        {/* Fade-out so long threads don't crash against the composer. */}
        <div
          aria-hidden
          className="pointer-events-none absolute inset-x-0 -top-10 h-10 bg-gradient-to-b from-transparent to-canvas"
        />
        <div className="mx-auto w-full max-w-[720px]">
          {error ? (
            <p
              role="alert"
              className="mb-2 rounded-lg border border-red-500/30 bg-red-500/10 px-3 py-2 text-xs text-red-700 dark:text-red-300"
              data-testid="chat-thread-error"
            >
              {error}
            </p>
          ) : null}
          {speechError ? (
            <p
              role="alert"
              className="mb-2 rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-800 dark:text-amber-200"
              data-testid="chat-speech-error"
            >
              {speechError}
            </p>
          ) : null}
          {voiceTranscript ? (
            <div
              className="mb-2 rounded-2xl border border-accent/30 bg-accent-soft/70 px-3.5 py-3"
              data-testid="chat-voice-transcript-review"
              role="status"
            >
              <div className="flex items-start gap-3">
                <span className="voice-waveform mt-1" aria-hidden>
                  <i /><i /><i /><i /><i />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="text-xs font-semibold text-ink">Check what Sir Thaddeus heard</p>
                  <p className="mt-1 text-xs leading-5 text-ink-muted">
                    Edit the transcript in the composer, then send when it is right.
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => {
                    setVoiceTranscript(null);
                    setDraft('');
                  }}
                  className="wiki-icon-button h-7 w-7"
                  aria-label="Cancel voice transcript"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              <button
                type="button"
                onClick={() => void onSubmit(draft)}
                disabled={!draft.trim() || blockedByPermission}
                className="btn-primary mt-2 min-h-9 px-3 text-xs"
              >
                <Send className="h-3.5 w-3.5" />
                Send transcript
              </button>
            </div>
          ) : null}
          {ephemeralMemory ? (
            <div
              className="mb-2 flex items-center gap-2 rounded-xl border border-violet-400/35 bg-violet-500/10 px-3 py-2 text-xs text-violet-800 dark:text-violet-200"
              role="status"
              data-testid="chat-incognito-status"
            >
              <EyeOff className="h-3.5 w-3.5 shrink-0" aria-hidden />
              <span>
                Incognito is on. Durable memory will not be read or written for this turn.
              </span>
            </div>
          ) : null}
          {blockedByPermission ? (
            <p className="mb-2 text-center text-[11px] text-amber-700 dark:text-amber-300">
              Approve or deny the inline permission request to continue.
            </p>
          ) : null}
          {blockedByPlan ? (
            <p className="mb-2 text-center text-[11px] text-accent" role="status">
              Review, edit, or cancel the inline plan before work begins.
            </p>
          ) : null}
          {steeringMode ? (
            <div
              className="mb-2 flex items-center justify-between rounded-xl border border-accent/30 bg-accent-soft/50 px-3 py-2 text-xs text-ink-muted"
              role="status"
              data-testid="run-steering-mode"
            >
              <span>
                {steeringMode === 'takeover'
                  ? 'Take over: describe what you did or how Sir Thaddeus should continue.'
                  : 'Redirect: tell Sir Thaddeus how the remaining work should change.'}
              </span>
              <button
                type="button"
                className="wiki-icon-button h-7 w-7"
                aria-label="Cancel steering"
                onClick={() => {
                  setSteeringMode(null);
                  setDraft('');
                  void resumeActiveRun();
                }}
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          ) : null}

          <ChatComposer
            value={draft}
            onChange={setDraft}
            onSubmit={onSubmit}
            sending={sending || blockedByPermission || blockedByPlan}
            placeholder={
              blockedByPermission
                ? 'Permission decision required above'
                : blockedByPlan
                  ? 'Plan approval required above'
                : steeringMode === 'takeover'
                  ? 'Describe your result or tell Sir Thaddeus what to do next'
                  : steeringMode === 'redirect'
                    ? 'How should the remaining work change?'
                : voiceTranscript
                  ? 'Correct the transcript before sending'
                  : 'Message Sir Thaddeus...'
            }
            inputTestId="chat-input"
            sendTestId="chat-send"
            rightActions={
              <>
                <button
                  type="button"
                  className={`chat-composer-icon-button ${
                    ephemeralMemory
                      ? 'border-violet-400 bg-violet-500/10 text-violet-800 dark:text-violet-200'
                      : ''
                  }`}
                  aria-label={ephemeralMemory ? 'Turn incognito off' : 'Turn incognito on'}
                  aria-pressed={ephemeralMemory}
                  title={
                    ephemeralMemory
                      ? 'Incognito on: durable memory is neither read nor written'
                      : 'Incognito off: normal memory policy applies'
                  }
                  data-testid="chat-incognito-toggle"
                  onClick={() => setEphemeralMemory((value) => !value)}
                >
                  <EyeOff className="h-4 w-4" strokeWidth={1.9} />
                </button>
                <button
                  type="button"
                  className={`chat-composer-icon-button ${
                    offlineMode
                      ? 'border-amber-400 bg-amber-500/10 text-amber-800 dark:text-amber-200'
                      : ''
                  }`}
                  aria-label={offlineMode ? 'Turn offline mode off' : 'Turn offline mode on'}
                  aria-pressed={offlineMode}
                  title={
                    offlineMode
                      ? 'Offline mode on: web-backed tools are blocked'
                      : 'Offline mode off: web-backed tools may run with permission'
                  }
                  data-testid="chat-offline-toggle"
                  disabled={offlineModeLoading || offlineModeSaving}
                  onClick={() => void toggleOfflineMode()}
                >
                  {offlineModeSaving ? (
                    <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.9} />
                  ) : (
                    <WifiOff className="h-4 w-4" strokeWidth={1.9} />
                  )}
                </button>
                <button
                  type="button"
                  className={`chat-composer-icon-button ${voiceState === 'recording' ? 'border-red-400 bg-red-500/10 text-red-700 dark:text-red-300' : ''}`}
                  aria-label={voiceButtonLabel(voiceState)}
                  title={voiceButtonLabel(voiceState)}
                  data-testid="chat-voice-hold"
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
                  {voiceState === 'starting' || voiceState === 'transcribing' || voiceState === 'sending' ? (
                    <Loader2 className="h-4 w-4 animate-spin" strokeWidth={1.9} />
                  ) : voiceState === 'recording' ? (
                    <span className="voice-waveform" aria-hidden><i /><i /><i /><i /><i /></span>
                  ) : (
                    <Mic className="h-4 w-4" strokeWidth={1.9} />
                  )}
                </button>
                <Link
                  to="/"
                  className="chat-composer-icon-button"
                  aria-label="New chat"
                  title="New chat"
                >
                  <Plus className="h-4 w-4" strokeWidth={1.9} />
                </Link>
              </>
            }
          />
        </div>
      </div>
    </section>
  );
}

interface MessageRowProps {
  role: 'user' | 'assistant' | 'system';
  text: string;
  sources?: ChatMessageSource[] | null;
  messageId?: string;
  threadId?: string;
  streaming?: boolean;
  startedAt?: number;
  isLatestAssistantResponse?: boolean;
  onRetryLatest?: () => void;
  retryDisabled?: boolean;
  speechStatus?: 'loading' | 'playing' | null;
  onSpeak?: () => void;
  runState?: import('@thaddeus/shared-types').TurnRunState;
  checkpoint?: string | null;
  plan?: import('@thaddeus/shared-types').WorkPlan | null;
  onPauseResume?: () => void;
  onRedirect?: () => void;
  onTakeOver?: () => void;
  onStop?: () => void;
  highlighted?: boolean;
  testId: string;
}

function MessageRow({
  role,
  text,
  sources,
  messageId,
  threadId,
  streaming,
  startedAt,
  isLatestAssistantResponse,
  onRetryLatest,
  retryDisabled,
  speechStatus,
  onSpeak,
  runState,
  checkpoint,
  plan,
  onPauseResume,
  onRedirect,
  onTakeOver,
  onStop,
  highlighted,
  testId,
}: MessageRowProps) {
  const normalized = String(role || '').toLowerCase();
  const isUser = normalized === 'user';
  const [copied, setCopied] = useState(false);
  const highlightClass = highlighted
    ? 'rounded-3xl ring-1 ring-accent/30 bg-accent-soft/40 px-3 py-2 transition-colors'
    : undefined;

  useEffect(() => {
    if (!copied) return;
    const timeout = window.setTimeout(() => setCopied(false), 1600);
    return () => window.clearTimeout(timeout);
  }, [copied]);

  const onCopy = async () => {
    try {
      await copyToClipboard(text);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  if (isUser) {
    return (
      <div
        data-testid={testId}
        data-role={role}
        data-streaming={streaming ? 'true' : undefined}
        className={highlightClass}
      >
        <div className="flex justify-end">
          <div className="max-w-[82%] whitespace-pre-wrap rounded-3xl rounded-tr-lg bg-canvas-sunken px-4 py-2.5 text-[15px] leading-6 text-ink">
            {text}
          </div>
        </div>
      </div>
    );
  }

  // Assistant messages flow into the page directly — no bubble, no avatar.
  // Tool activity pills (if any fired during this turn) float above the
  // text so the reader sees what the model did before reading what it said.
  const showActions = !streaming && text.trim().length > 0;
  return (
    <div
      data-testid={testId}
      data-role={role}
      data-streaming={streaming ? 'true' : undefined}
      className={highlightClass}
    >
      {streaming && messageId && onPauseResume && onRedirect && onTakeOver && onStop ? (
        <SteerableProgressCard
          messageId={messageId}
          startedAt={startedAt}
          hasVisibleText={Boolean(text.trim())}
          runState={runState}
          checkpoint={checkpoint}
          plan={plan}
          onPauseResume={onPauseResume}
          onRedirect={onRedirect}
          onTakeOver={onTakeOver}
          onStop={onStop}
        />
      ) : null}
      {text.trim() ? (
        <div aria-live={streaming ? 'off' : undefined}>
          <Markdown>{text}</Markdown>
        </div>
      ) : streaming ? (
        <span className="sr-only" data-testid="chat-streaming-placeholder">
          Assistant response in progress
        </span>
      ) : null}
      {sources && sources.length > 0 ? <SourceCards sources={sources} /> : null}
      {showActions ? (
        <div
          className="mt-3 flex items-center gap-1 text-ink-subtle"
          data-testid={isLatestAssistantResponse ? 'chat-latest-response-actions' : 'chat-response-actions'}
        >
          <button
            type="button"
            onClick={onSpeak}
            data-testid="chat-speak-response"
            className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent transition hover:border-line hover:bg-canvas-sunken hover:text-ink disabled:cursor-not-allowed disabled:opacity-45"
            aria-label={speechStatus ? 'Stop reading response aloud' : 'Read response aloud'}
            title={speechStatus ? 'Stop' : 'Read aloud'}
            disabled={!onSpeak}
          >
            {speechStatus === 'loading' ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" strokeWidth={1.9} />
            ) : speechStatus === 'playing' ? (
              <Square className="h-3.5 w-3.5" strokeWidth={1.9} />
            ) : (
              <Volume2 className="h-3.5 w-3.5" strokeWidth={1.9} />
            )}
          </button>
          {isLatestAssistantResponse ? (
            <>
              <button
                type="button"
                onClick={() => { void onCopy(); }}
                className="receipt-action"
                aria-label="Copy latest response"
                data-testid="chat-copy-latest-response"
              >
                {copied ? <Check className="h-3.5 w-3.5" /> : <Clipboard className="h-3.5 w-3.5" />}
                {copied ? 'Copied' : 'Copy'}
              </button>
              {onRetryLatest ? (
                <button
                  type="button"
                  onClick={onRetryLatest}
                  disabled={retryDisabled}
                  className="receipt-action"
                  aria-label="Retry latest response"
                  data-testid="chat-retry-latest-response"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Retry
                  <kbd className="text-[9px] opacity-60">R</kbd>
                </button>
              ) : null}
            </>
          ) : null}
        </div>
      ) : null}
      {!streaming && messageId && text.trim() ? (
        <WorkReceipt
          messageId={messageId}
          threadId={threadId}
          text={text}
          sources={sources}
          onRetry={onRetryLatest}
          retryDisabled={retryDisabled}
        />
      ) : null}
      {streaming && text.trim() ? (
        <span
          className="ml-0.5 inline-block h-[1.1em] w-[2px] translate-y-1 animate-pulse bg-accent align-middle"
          aria-hidden
        />
      ) : null}
    </div>
  );
}

function locateLatestMessageEl(container: HTMLElement): HTMLElement | null {
  // While the assistant is streaming, the streaming row is what the reader
  // is following — anchor scroll math to it so we react to its growing
  // height in real time.
  const streaming = container.querySelector<HTMLElement>('[data-streaming="true"]');
  if (streaming) return streaming;

  // Otherwise pin to the latest assistant message. Fall through to the
  // very last message of any role if there are no assistant rows yet.
  const assistantRows = container.querySelectorAll<HTMLElement>('[data-role="assistant"]');
  if (assistantRows.length > 0) return assistantRows[assistantRows.length - 1] ?? null;
  const anyRows = container.querySelectorAll<HTMLElement>('[data-role]');
  return anyRows.length > 0 ? anyRows[anyRows.length - 1] ?? null : null;
}

async function copyToClipboard(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  textarea.select();
  document.execCommand('copy');
  document.body.removeChild(textarea);
}

function mediaRecorderOptions(): MediaRecorderOptions {
  for (const mimeType of ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus']) {
    if (MediaRecorder.isTypeSupported(mimeType)) return { mimeType };
  }
  return {};
}

// Splits a TTS body into chunks small enough that synthesizing each one is
// fast (so the user hears the first audio quickly) but large enough that
// prosody isn't choppy. We prefer to break on sentence terminators; only
// fall back to length-based slicing for runaway sentences.
function chunkSpeechText(text: string): string[] {
  const trimmed = text.trim();
  if (!trimmed) return [trimmed];
  const HARD_MAX = 220;
  const MIN_FIRST = 80;

  // Quick split on sentence boundaries; the regex preserves the punctuation.
  const parts = trimmed
    .split(/(?<=[.!?])\s+(?=[A-Z0-9"'([])/)
    .map((s) => s.trim())
    .filter(Boolean);

  // Coalesce so chunks are reasonable; further-split any overlong sentence.
  const out: string[] = [];
  let buffer = '';
  for (const part of parts) {
    const candidate = buffer ? `${buffer} ${part}` : part;
    if (candidate.length <= HARD_MAX) {
      buffer = candidate;
      // Keep the first chunk small so the user hears audio fast.
      if (out.length === 0 && buffer.length >= MIN_FIRST) {
        out.push(buffer);
        buffer = '';
      }
    } else {
      if (buffer) {
        out.push(buffer);
        buffer = '';
      }
      if (part.length <= HARD_MAX) {
        buffer = part;
      } else {
        for (let i = 0; i < part.length; i += HARD_MAX) {
          out.push(part.slice(i, i + HARD_MAX));
        }
      }
    }
  }
  if (buffer) out.push(buffer);
  return out.length > 0 ? out : [trimmed];
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

function releaseMicCapture(
  recorderRef: React.MutableRefObject<MediaRecorder | null>,
  streamRef: React.MutableRefObject<MediaStream | null>,
): void {
  const recorder = recorderRef.current;
  if (recorder && recorder.state !== 'inactive') {
    try { recorder.stop(); } catch { /* best effort */ }
  }
  recorderRef.current = null;
  // Intentionally do NOT stop the underlying MediaStream here \u2014 the
  // chat route keeps it warm in warmStreamRef so subsequent PTT presses
  // skip the getUserMedia spinner. The route unmount cleanup is the
  // single owner that actually stops the tracks.
  streamRef.current = null;
}

function voiceButtonLabel(state: 'idle' | 'starting' | 'recording' | 'transcribing' | 'sending'): string {
  if (state === 'starting') return 'Starting microphone';
  if (state === 'recording') return 'Release to send voice message';
  if (state === 'transcribing') return 'Transcribing voice message';
  if (state === 'sending') return 'Sending voice message';
  return 'Hold to talk';
}

function releaseSpeechAudio(
  audioRef: React.MutableRefObject<HTMLAudioElement | null>,
  audioUrlRef: React.MutableRefObject<string | null>,
): void {
  if (audioRef.current) {
    audioRef.current.pause();
    audioRef.current.removeAttribute('src');
    audioRef.current.load();
    audioRef.current = null;
  }
  if (audioUrlRef.current) {
    URL.revokeObjectURL(audioUrlRef.current);
    audioUrlRef.current = null;
  }
}
