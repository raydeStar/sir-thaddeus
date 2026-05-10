import { memo } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { isExternalUrl, openExternalUrl } from '../lib/externalLinks';

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
          // External links go through the runtime so Photino hands them to the OS.
          a: ({ node: _node, href, ...props }) => (
            <a
              {...props}
              href={href}
              rel="noopener noreferrer"
              onClick={(event) => {
                if (!isExternalUrl(href)) return;
                event.preventDefault();
                void openExternalUrl(href);
              }}
              onAuxClick={(event) => {
                if (!isExternalUrl(href)) return;
                event.preventDefault();
                void openExternalUrl(href);
              }}
            />
          ),
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
});
