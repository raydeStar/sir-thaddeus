import { useEffect, useMemo, useRef, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { EditorContent, useEditor } from '@tiptap/react';
import LinkExtension from '@tiptap/extension-link';
import StarterKit from '@tiptap/starter-kit';
import { DOMSerializer } from '@tiptap/pm/model';
import { marked } from 'marked';
import TurndownService from 'turndown';
import {
  Bold,
  Code,
  Heading1,
  Heading2,
  Italic,
  Link as LinkIcon,
  List,
  ListOrdered,
  Pilcrow,
  Quote,
  Unlink,
} from 'lucide-react';

interface WikiMarkdownEditorProps {
  markdown: string;
  disabled: boolean;
  onChange: (markdown: string) => void;
  onSelectionChange?: (selectedText: string) => void;
}

export function WikiMarkdownEditor({ markdown, disabled, onChange, onSelectionChange }: WikiMarkdownEditorProps) {
  const applyingExternalContent = useRef(false);
  const lastExternalMarkdown = useRef(markdown);
  const [linkDialog, setLinkDialog] = useState<{ href: string; text: string; hasSelection: boolean; hasExistingLink: boolean } | null>(null);
  const turndown = useMemo(() => {
    const service = new TurndownService({
      bulletListMarker: '-',
      codeBlockStyle: 'fenced',
      headingStyle: 'atx',
    });
    service.keep(['table', 'thead', 'tbody', 'tr', 'th', 'td']);
    return service;
  }, []);

  // Tiptap v3 `useEditor` destroys and recreates the editor whenever a
  // non-empty deps array changes. If the editor is rebuilt mid-drag, the
  // browser selection is wiped and the user only ends up with whatever was
  // selected at the very first mousemove tick (often a single character).
  // Stabilize the callbacks behind refs and pass an empty deps array so the
  // editor instance survives for the lifetime of the component.
  const onChangeRef = useRef(onChange);
  const onSelectionChangeRef = useRef(onSelectionChange);
  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);
  useEffect(() => {
    onSelectionChangeRef.current = onSelectionChange;
  }, [onSelectionChange]);

  const editor = useEditor({
    extensions: [
      StarterKit.configure({
        heading: { levels: [1, 2, 3] },
        link: false,
      }),
      LinkExtension.configure({
        autolink: true,
        linkOnPaste: true,
        openOnClick: false,
        HTMLAttributes: {
          rel: 'noopener noreferrer',
          target: '_blank',
        },
      }),
    ],
    content: markdownToHtml(markdown),
    editable: !disabled,
    editorProps: {
      attributes: {
        class: 'wiki-editor-content prose-thaddeus min-h-full max-w-none px-5 py-5 outline-none md:px-8',
      },
    },
    onUpdate: ({ editor: updatedEditor }) => {
      if (applyingExternalContent.current) return;
      const nextMarkdown = turndown.turndown(updatedEditor.getHTML());
      lastExternalMarkdown.current = nextMarkdown;
      onChangeRef.current(nextMarkdown);
    },
    onSelectionUpdate: ({ editor: updatedEditor }) => {
      const { from, to, empty } = updatedEditor.state.selection;
      if (empty) {
        onSelectionChangeRef.current?.('');
        return;
      }

      // Serialize the selected slice through the same HTML→markdown pipeline
      // used for the full document so the resulting text matches verbatim
      // against the persisted page markdown (preserving italics, links,
      // headings, etc.). This is what the backend uses to locate the passage
      // it must replace; sending plain text would lose markdown markers and
      // cause "selected text no longer matches" errors.
      let selectedMarkdown = '';
      try {
        const slice = updatedEditor.state.doc.slice(from, to);
        const serializer = DOMSerializer.fromSchema(updatedEditor.schema);
        const fragment = serializer.serializeFragment(slice.content);
        const container = document.createElement('div');
        container.appendChild(fragment);
        selectedMarkdown = turndown.turndown(container.innerHTML).trim();
      } catch {
        selectedMarkdown = '';
      }

      if (!selectedMarkdown) {
        selectedMarkdown = updatedEditor.state.doc.textBetween(from, to, '\n\n').trim();
      }

      onSelectionChangeRef.current?.(selectedMarkdown);
    },
  }, []);

  const applyLink = () => {
    if (!editor) return;
    const currentHref = (editor.getAttributes('link').href as string | undefined) ?? '';
    let initialText = '';
    if (editor.state.selection.empty && !currentHref) {
      initialText = '';
    } else if (currentHref) {
      // Extend mark range so the existing text is captured for editing.
      const { from, to } = editor.state.selection;
      initialText = editor.state.doc.textBetween(from, to, '\n').trim();
    } else {
      const { from, to } = editor.state.selection;
      initialText = editor.state.doc.textBetween(from, to, '\n').trim();
    }
    setLinkDialog({ href: currentHref, text: initialText, hasSelection: !editor.state.selection.empty, hasExistingLink: Boolean(currentHref) });
  };

  const closeLinkDialog = () => setLinkDialog(null);

  const handleLinkSubmit = (href: string, text: string) => {
    if (!editor) {
      setLinkDialog(null);
      return;
    }
    const trimmedHref = href.trim();
    if (!trimmedHref) {
      editor.chain().focus().extendMarkRange('link').unsetLink().run();
      setLinkDialog(null);
      return;
    }
    if (!isValidLinkHref(trimmedHref)) {
      // Surface validation by leaving the dialog open — caller already shows the message.
      return;
    }
    if (editor.state.selection.empty && !linkDialog?.hasExistingLink) {
      const label = text.trim() || trimmedHref;
      editor.chain().focus().insertContent({
        type: 'text',
        text: label,
        marks: [{ type: 'link', attrs: { href: trimmedHref } }],
      }).run();
    } else if (linkDialog?.hasExistingLink) {
      // Editing an existing link: replace its text + href across the full mark range.
      const chain = editor.chain().focus().extendMarkRange('link');
      const label = text.trim();
      if (label) {
        chain.insertContent({
          type: 'text',
          text: label,
          marks: [{ type: 'link', attrs: { href: trimmedHref } }],
        }).run();
      } else {
        chain.setLink({ href: trimmedHref }).run();
      }
    } else {
      editor.chain().focus().extendMarkRange('link').setLink({ href: trimmedHref }).run();
    }
    setLinkDialog(null);
  };

  const handleLinkRemove = () => {
    if (!editor) {
      setLinkDialog(null);
      return;
    }
    editor.chain().focus().extendMarkRange('link').unsetLink().run();
    setLinkDialog(null);
  };

  useEffect(() => {
    if (!editor) return;
    editor.setEditable(!disabled);
  }, [disabled, editor]);

  useEffect(() => {
    if (!editor) return;
    if (markdown === lastExternalMarkdown.current) return;
    applyingExternalContent.current = true;
    editor.commands.setContent(markdownToHtml(markdown), { emitUpdate: false });
    lastExternalMarkdown.current = markdown;
    applyingExternalContent.current = false;
  }, [editor, markdown]);

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      <div className="flex min-h-11 items-center gap-2 overflow-x-auto border-b border-line px-3 py-1.5" role="toolbar" aria-label="Markdown formatting">
        <ToolbarGroup label="Block style">
          <ToolbarButton label="Paragraph" active={editor?.isActive('paragraph') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().setParagraph().run()}>
            <Pilcrow className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Heading 1" active={editor?.isActive('heading', { level: 1 }) ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleHeading({ level: 1 }).run()}>
            <Heading1 className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Heading 2" active={editor?.isActive('heading', { level: 2 }) ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleHeading({ level: 2 }).run()}>
            <Heading2 className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
        </ToolbarGroup>
        <ToolbarGroup label="Inline formatting">
          <ToolbarButton label="Bold" active={editor?.isActive('bold') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleBold().run()}>
            <Bold className="h-4 w-4" strokeWidth={1.9} />
          </ToolbarButton>
          <ToolbarButton label="Italic" active={editor?.isActive('italic') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleItalic().run()}>
            <Italic className="h-4 w-4" strokeWidth={1.9} />
          </ToolbarButton>
          <ToolbarButton label="Code" active={editor?.isActive('codeBlock') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleCodeBlock().run()}>
            <Code className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Link" active={editor?.isActive('link') ?? false} disabled={!editor || disabled} onClick={applyLink}>
            <LinkIcon className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Remove link" active={false} disabled={!editor || disabled || !editor.isActive('link')} onClick={() => editor?.chain().focus().extendMarkRange('link').unsetLink().run()}>
            <Unlink className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
        </ToolbarGroup>
        <ToolbarGroup label="Lists and quote">
          <ToolbarButton label="Bullet list" active={editor?.isActive('bulletList') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleBulletList().run()}>
            <List className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Numbered list" active={editor?.isActive('orderedList') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleOrderedList().run()}>
            <ListOrdered className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
          <ToolbarButton label="Quote" active={editor?.isActive('blockquote') ?? false} disabled={!editor || disabled} onClick={() => editor?.chain().focus().toggleBlockquote().run()}>
            <Quote className="h-4 w-4" strokeWidth={1.8} />
          </ToolbarButton>
        </ToolbarGroup>
      </div>
      <EditorContent editor={editor} className="wiki-editor min-h-0 flex-1 overflow-y-auto" />
      {linkDialog ? (
        <WikiLinkDialog
          initialHref={linkDialog.href}
          initialText={linkDialog.text}
          allowTextEdit={!linkDialog.hasSelection || linkDialog.hasExistingLink}
          canRemove={linkDialog.hasExistingLink}
          onCancel={closeLinkDialog}
          onSubmit={handleLinkSubmit}
          onRemove={handleLinkRemove}
        />
      ) : null}
    </div>
  );
}

function ToolbarGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div role="group" aria-label={label} className="flex h-9 shrink-0 items-center gap-0.5 rounded-xl border border-line bg-canvas-raised p-0.5">
      {children}
    </div>
  );
}

function ToolbarButton({
  label,
  active,
  disabled,
  onClick,
  children,
}: {
  label: string;
  active: boolean;
  disabled: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      className={`inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border transition disabled:cursor-not-allowed disabled:opacity-55 ${active ? 'border-accent bg-accent-soft text-ink' : 'border-transparent text-ink-muted hover:border-line hover:bg-canvas-raised hover:text-ink'}`}
      title={label}
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

function markdownToHtml(markdown: string): string {
  return marked.parse(markdown || '', { async: false }) as string;
}

export function isValidLinkHref(href: string): boolean {
  const trimmed = href.trim();
  if (!trimmed) return false;
  // Allow internal page anchors and relative-root paths (e.g. #section, /pages/foo).
  if (trimmed.startsWith('#') || trimmed.startsWith('/')) return true;
  // Allow common scheme-less hostnames by parsing against an https base.
  try {
    const parsed = new URL(trimmed, 'https://placeholder.local/');
    if (!parsed.protocol) return false;
    const allowed = new Set(['http:', 'https:', 'mailto:', 'ftp:', 'ftps:', 'tel:', 'sms:']);
    return allowed.has(parsed.protocol);
  } catch {
    return false;
  }
}

function WikiLinkDialog({
  initialHref,
  initialText,
  allowTextEdit,
  canRemove,
  onCancel,
  onSubmit,
  onRemove,
}: {
  initialHref: string;
  initialText: string;
  allowTextEdit: boolean;
  canRemove: boolean;
  onCancel: () => void;
  onSubmit: (href: string, text: string) => void;
  onRemove: () => void;
}) {
  const [href, setHref] = useState(initialHref);
  const [text, setText] = useState(initialText);
  const [touched, setTouched] = useState(false);
  const hrefRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    hrefRef.current?.focus();
    hrefRef.current?.select();
  }, []);

  const trimmed = href.trim();
  const valid = trimmed.length === 0 ? false : isValidLinkHref(trimmed);
  const showError = touched && trimmed.length > 0 && !valid;

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setTouched(true);
    if (!valid) return;
    onSubmit(trimmed, text);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/35 px-4" role="presentation" onMouseDown={onCancel}>
      <form
        role="dialog"
        aria-modal="true"
        aria-labelledby="wiki-link-title"
        className="w-full max-w-sm rounded-xl border border-line bg-canvas p-4 shadow-xl"
        onMouseDown={(event) => event.stopPropagation()}
        onSubmit={handleSubmit}
      >
        <h2 id="wiki-link-title" className="text-base font-semibold text-ink">{canRemove ? 'Edit link' : 'Insert link'}</h2>
        <label className="mt-3 block text-xs font-medium text-ink-muted">
          URL
          <input
            ref={hrefRef}
            type="text"
            inputMode="url"
            autoComplete="off"
            spellCheck={false}
            className={`mt-1 w-full rounded-md border px-2 py-1.5 text-sm text-ink outline-none transition focus:ring-2 focus:ring-accent/40 ${showError ? 'border-rose-500' : 'border-line bg-canvas-raised'}`}
            value={href}
            placeholder="https://example.com"
            onChange={(event) => setHref(event.target.value)}
            onBlur={() => setTouched(true)}
          />
        </label>
        {showError ? (
          <p className="mt-1 text-xs text-rose-600">Enter a valid URL (http, https, mailto, etc.).</p>
        ) : null}
        {allowTextEdit ? (
          <label className="mt-3 block text-xs font-medium text-ink-muted">
            Display text
            <input
              type="text"
              autoComplete="off"
              spellCheck={false}
              className="mt-1 w-full rounded-md border border-line bg-canvas-raised px-2 py-1.5 text-sm text-ink outline-none transition focus:ring-2 focus:ring-accent/40"
              value={text}
              placeholder="Optional — defaults to URL"
              onChange={(event) => setText(event.target.value)}
            />
          </label>
        ) : null}
        <div className="mt-4 flex flex-wrap justify-end gap-2">
          {canRemove ? (
            <button type="button" className="wiki-command-button border-rose-500 text-rose-600 hover:bg-rose-500/10" onClick={onRemove}>
              Remove link
            </button>
          ) : null}
          <button type="button" className="wiki-command-button" onClick={onCancel}>Cancel</button>
          <button
            type="submit"
            className="wiki-command-button border-accent bg-accent text-white hover:bg-accent hover:border-accent disabled:opacity-60"
            disabled={!valid}
          >
            {canRemove ? 'Update' : 'Insert'}
          </button>
        </div>
      </form>
    </div>
  );
}