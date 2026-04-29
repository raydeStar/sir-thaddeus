import { useEffect, useMemo, useRef } from 'react';
import type { ReactNode } from 'react';
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
  const turndown = useMemo(() => {
    const service = new TurndownService({
      bulletListMarker: '-',
      codeBlockStyle: 'fenced',
      headingStyle: 'atx',
    });
    service.keep(['table', 'thead', 'tbody', 'tr', 'th', 'td']);
    return service;
  }, []);

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
      onChange(nextMarkdown);
    },
    onSelectionUpdate: ({ editor: updatedEditor }) => {
      const { from, to, empty } = updatedEditor.state.selection;
      if (empty) {
        onSelectionChange?.('');
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

      onSelectionChange?.(selectedMarkdown);
    },
  }, [turndown, onChange, onSelectionChange]);

  const applyLink = () => {
    if (!editor) return;
    const currentHref = editor.getAttributes('link').href as string | undefined;
    const href = window.prompt('Link URL', currentHref ?? '');
    if (href === null) return;

    const trimmedHref = href.trim();
    if (!trimmedHref) {
      editor.chain().focus().extendMarkRange('link').unsetLink().run();
      return;
    }

    if (editor.state.selection.empty) {
      const label = window.prompt('Link text', trimmedHref);
      if (label === null) return;
      const text = label.trim() || trimmedHref;
      editor.chain().focus().insertContent({
        type: 'text',
        text,
        marks: [{ type: 'link', attrs: { href: trimmedHref } }],
      }).run();
      return;
    }

    editor.chain().focus().extendMarkRange('link').setLink({ href: trimmedHref }).run();
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