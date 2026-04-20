import { createFileRoute } from '@tanstack/react-router';
import { useEffect, useRef, useState } from 'react';
import { PageScaffold } from '../components/PageScaffold';
import { useChatStore } from '../stores/chatStore';

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

  const [draft, setDraft] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    void openThread(threadId);
  }, [openThread, threadId]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: 'smooth' });
  }, [thread?.messages.length, activeTurn?.text]);

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!draft.trim() || sending) return;
    const text = draft;
    setDraft('');
    await send(text);
  };

  const messages = thread?.messages ?? [];

  return (
    <PageScaffold
      testId="route-chat-thread"
      title={thread?.title ?? 'Loading…'}
      subtitle="Send a message and the assistant will reply."
    >
      <div
        ref={scrollRef}
        data-testid="chat-message-list"
        className="mb-4 max-h-[60vh] space-y-3 overflow-y-auto pr-1"
      >
        {messages.map((m) => (
          <MessageBubble key={m.id} role={m.role} text={m.text} testId={`chat-message-${m.id}`} />
        ))}
        {activeTurn ? (
          <MessageBubble
            role="assistant"
            text={activeTurn.text || '…'}
            streaming
            testId="chat-message-streaming"
          />
        ) : null}
        {messages.length === 0 && !activeTurn ? (
          <p className="text-sm italic text-slate-400" data-testid="chat-thread-empty">
            No messages yet. Say hello.
          </p>
        ) : null}
      </div>

      {error ? (
        <p className="mb-2 text-sm text-red-600" data-testid="chat-thread-error">
          {error}
        </p>
      ) : null}

      <form onSubmit={onSubmit} className="flex gap-2" data-testid="chat-composer">
        <input
          type="text"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="Type a message…"
          data-testid="chat-input"
          className="flex-1 rounded-md border border-slate-300 px-3 py-2 text-sm focus:border-thaddeus-ink focus:outline-none"
          disabled={sending}
        />
        <button
          type="submit"
          data-testid="chat-send"
          disabled={sending || !draft.trim()}
          className="rounded-md bg-thaddeus-ink px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
        >
          Send
        </button>
      </form>
    </PageScaffold>
  );
}

interface MessageBubbleProps {
  role: 'user' | 'assistant' | 'system';
  text: string;
  streaming?: boolean;
  testId: string;
}

function MessageBubble({ role, text, streaming, testId }: MessageBubbleProps) {
  const isUser = role === 'user';
  return (
    <div
      data-testid={testId}
      data-role={role}
      data-streaming={streaming ? 'true' : undefined}
      className={`flex ${isUser ? 'justify-end' : 'justify-start'}`}
    >
      <div
        className={`max-w-[80%] whitespace-pre-wrap rounded-lg px-3 py-2 text-sm ${
          isUser
            ? 'bg-thaddeus-ink text-white'
            : 'border border-slate-200 bg-slate-50 text-thaddeus-ink'
        }`}
      >
        {text}
        {streaming ? <span className="ml-1 animate-pulse">▍</span> : null}
      </div>
    </div>
  );
}
