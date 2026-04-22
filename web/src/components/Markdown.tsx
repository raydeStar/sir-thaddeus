import { memo } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface MarkdownProps {
  children: string;
}

/**
 * Renders assistant messages as Markdown with GitHub-flavored extensions
 * (tables, strikethrough, task lists). Styling comes from Tailwind's
 * typography plugin — see the `prose-thaddeus` class below.
 *
 * Memoized so streaming updates don't re-parse the whole tree every token
 * (react-markdown caches on children reference; memo adds a belt).
 */
export const Markdown = memo(function Markdown({ children }: MarkdownProps) {
  return (
    <div className="prose-thaddeus">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          // External links get target+rel so accidental clicks don't replace the app.
          a: ({ node: _node, ...props }) => (
            <a
              {...props}
              target="_blank"
              rel="noopener noreferrer"
            />
          ),
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
});
