import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ArrowUp, MessageSquareText, Mic, Workflow, type LucideIcon } from 'lucide-react';
import { useChatStore } from '../stores/chatStore';

export const Route = createFileRoute('/')({
  component: HomeRoute,
});

function HomeRoute() {
  const navigate = useNavigate();
  const newThread = useChatStore((s) => s.newThread);
  const send = useChatStore((s) => s.send);

  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);

  const start = async () => {
    if (!draft.trim() || busy) return;
    setBusy(true);
    try {
      const t = await newThread();
      void navigate({ to: '/chat/$threadId', params: { threadId: t.id } });
      // Open the thread in the store so send() targets it, then post.
      await useChatStore.getState().openThread(t.id);
      await send(draft.trim());
      setDraft('');
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void start();
    }
  };

  return (
    <section
      data-testid="route-home"
      className="mx-auto flex h-full w-full max-w-2xl flex-col items-center justify-center px-6"
    >
      <div className="w-full">
        <h1 className="text-center text-3xl font-semibold tracking-tightest text-ink md:text-[2.25rem]">
          Good to see you.
        </h1>
        <p className="mt-2 text-center text-sm text-ink-muted">
          What can Sir Thaddeus help you with today?
        </p>

        <form
          onSubmit={(e) => {
            e.preventDefault();
            void start();
          }}
          className="mt-8"
        >
          <div className="surface flex items-end gap-2 px-3 py-2.5">
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={onKeyDown}
              placeholder="Ask anything…"
              rows={1}
              data-testid="home-prompt"
              className="min-h-[28px] flex-1 resize-none border-0 bg-transparent px-2 py-1.5 text-[15px] leading-6 text-ink placeholder:text-ink-subtle focus:outline-none"
            />
            <button
              type="submit"
              disabled={!draft.trim() || busy}
              aria-label="Send"
              className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-accent text-white transition hover:opacity-90 disabled:opacity-30"
            >
              <ArrowUp className="h-4 w-4" strokeWidth={2.25} />
            </button>
          </div>
        </form>

        <div className="mt-6 flex flex-wrap items-center justify-center gap-2">
          <QuickAction
            icon={MessageSquareText}
            label="Open chat"
            onClick={() => void navigate({ to: '/chat' })}
          />
          <QuickAction
            icon={Workflow}
            label="Automations"
            onClick={() => void navigate({ to: '/automations' })}
          />
          <QuickAction
            icon={Mic}
            label="Voice (soon)"
            onClick={() => void navigate({ to: '/settings' })}
          />
        </div>
      </div>
    </section>
  );
}

function QuickAction({
  icon: Icon,
  label,
  onClick,
}: {
  icon: LucideIcon;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas-raised px-3 py-1.5 text-xs text-ink-muted shadow-soft transition hover:bg-accent-soft hover:text-ink"
    >
      <Icon className="h-3.5 w-3.5" strokeWidth={1.75} />
      {label}
    </button>
  );
}
