import { createFileRoute, Link } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, Check, Copy, Loader2, Plus, RotateCcw, Square, Volume2 } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';
import { Markdown } from '../components/Markdown';
import { SourceCards } from '../components/SourceCards';
import { ToolActivityPills } from '../components/ToolActivityPills';
import { FootmanDecisionChip } from '../components/FootmanDecisionChip';
import { ChatComposer, type WikiContextSelection } from '../components/ChatComposer';
import { synthesizeSpeech } from '../lib/voiceApi';
import type { ChatMessageSource } from '@thaddeus/shared-types';

export const Route = createFileRoute('/chat/$threadId')({
  component: ChatThreadRoute,
});

function ChatThreadRoute() {
  const { threadId } = Route.useParams();
  const thread = useChatStore((s) => s.activeThread);
  const activeTurn = useChatStore((s) => s.activeTurn);
  const sending = useChatStore((s) => s.sending);
  const error = useChatStore((s) => s.error);
  const openThread = useChatStore((s) => s.openThread);
  const send = useChatStore((s) => s.send);
  const retryLatestResponse = useChatStore((s) => s.retryLatestResponse);

  const [draft, setDraft] = useState('');
  const [speechError, setSpeechError] = useState<string | null>(null);
  const [speechState, setSpeechState] = useState<{ messageId: string; status: 'loading' | 'playing' } | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const audioUrlRef = useRef<string | null>(null);
  const speechRequestRef = useRef(0);

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

  useEffect(() => () => {
    speechRequestRef.current += 1;
    releaseSpeechAudio(audioRef, audioUrlRef);
  }, []);

  const onSubmit = async (text: string, wikiContext?: WikiContextSelection) => {
    if (sending) return;
    setDraft('');
    await send(text, wikiContext);
  };

  const stopSpeech = () => {
    speechRequestRef.current += 1;
    releaseSpeechAudio(audioRef, audioUrlRef);
    setSpeechState(null);
  };

  const onSpeakMessage = async (messageId: string, text: string) => {
    if (speechState?.messageId === messageId) {
      stopSpeech();
      return;
    }

    const requestId = speechRequestRef.current + 1;
    speechRequestRef.current = requestId;
    releaseSpeechAudio(audioRef, audioUrlRef);
    setSpeechError(null);
    setSpeechState({ messageId, status: 'loading' });

    try {
      const audioBlob = await synthesizeSpeech(text);
      if (speechRequestRef.current !== requestId) return;

      const url = URL.createObjectURL(audioBlob);
      audioUrlRef.current = url;
      const audio = new Audio(url);
      audioRef.current = audio;
      audio.addEventListener('ended', stopSpeech, { once: true });
      audio.addEventListener('error', () => {
        setSpeechError('Could not play the synthesized audio.');
        stopSpeech();
      }, { once: true });

      setSpeechState({ messageId, status: 'playing' });
      await audio.play();
    } catch (e) {
      if (speechRequestRef.current === requestId) {
        stopSpeech();
        setSpeechError((e as Error).message || 'Could not read that response aloud.');
      }
    }
  };

  const messages = thread?.messages ?? [];
  const latestMessage = messages[messages.length - 1];
  const latestAssistantResponseId =
    !activeTurn && String(latestMessage?.role || '').toLowerCase() === 'assistant' && latestMessage?.text?.trim()
      ? latestMessage.id
      : null;
  const empty = messages.length === 0 && !activeTurn;

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
                    isLatestAssistantResponse={m.id === latestAssistantResponseId}
                    onRetryLatest={() => void retryLatestResponse()}
                    retryDisabled={sending || Boolean(activeTurn)}
                    speechStatus={speechState?.messageId === m.id ? speechState.status : null}
                    onSpeak={() => void onSpeakMessage(m.id, m.text)}
                    testId={`chat-message-${m.id}`}
                  />
                );
              })}
              {activeTurn ? (
                <MessageRow
                  role="assistant"
                  text={activeTurn.text || ''}
                  messageId={activeTurn.messageId}
                  streaming
                  testId="chat-message-streaming"
                />
              ) : null}
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

          <ChatComposer
            value={draft}
            onChange={setDraft}
            onSubmit={onSubmit}
            sending={sending}
            inputTestId="chat-input"
            sendTestId="chat-send"
            rightActions={
              <Link
                to="/"
                className="chat-composer-icon-button"
                aria-label="New chat"
                title="New chat"
              >
                <Plus className="h-4 w-4" strokeWidth={1.9} />
              </Link>
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
  streaming?: boolean;
  isLatestAssistantResponse?: boolean;
  onRetryLatest?: () => void;
  retryDisabled?: boolean;
  speechStatus?: 'loading' | 'playing' | null;
  onSpeak?: () => void;
  testId: string;
}

function MessageRow({
  role,
  text,
  sources,
  messageId,
  streaming,
  isLatestAssistantResponse,
  onRetryLatest,
  retryDisabled,
  speechStatus,
  onSpeak,
  testId,
}: MessageRowProps) {
  const normalized = String(role || '').toLowerCase();
  const isUser = normalized === 'user';
  const [copied, setCopied] = useState(false);

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
        className="flex justify-end"
      >
        <div className="max-w-[82%] whitespace-pre-wrap rounded-3xl rounded-tr-lg bg-canvas-sunken px-4 py-2.5 text-[15px] leading-6 text-ink">
          {text}
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
    >
      {messageId ? <FootmanDecisionChip messageId={messageId} /> : null}
      {messageId ? <ToolActivityPills messageId={messageId} /> : null}
      <Markdown>{text}</Markdown>
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
                onClick={onCopy}
                data-testid="chat-copy-latest-response"
                className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent transition hover:border-line hover:bg-canvas-sunken hover:text-ink"
                aria-label={copied ? 'Copied latest response' : 'Copy latest response'}
                title={copied ? 'Copied' : 'Copy'}
              >
                {copied ? <Check className="h-3.5 w-3.5" strokeWidth={2} /> : <Copy className="h-3.5 w-3.5" strokeWidth={1.9} />}
              </button>
              <button
                type="button"
                onClick={onRetryLatest}
                disabled={retryDisabled}
                data-testid="chat-retry-latest-response"
                className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-transparent transition hover:border-line hover:bg-canvas-sunken hover:text-ink disabled:cursor-not-allowed disabled:opacity-45"
                aria-label="Retry latest response"
                title="Retry"
              >
                <RotateCcw className="h-3.5 w-3.5" strokeWidth={1.9} />
              </button>
            </>
          ) : null}
        </div>
      ) : null}
      {streaming ? (
        <span
          className="ml-0.5 inline-block h-[1.1em] w-[2px] translate-y-1 animate-pulse bg-accent align-middle"
          aria-hidden
        />
      ) : null}
    </div>
  );
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
