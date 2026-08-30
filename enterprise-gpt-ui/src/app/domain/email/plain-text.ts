/**
 * Flattens answer markdown into the plain text a mail body is.
 *
 * `mailto:` carries no formatting, so shipping the source through untouched puts
 * literal `**asterisks**` and `[label](url)` into the draft the user sends. This
 * is deliberately a formatter rather than a parser: it runs on text a mail client
 * will show verbatim, where a wrong guess is a visible defect in someone's
 * outgoing mail, so every rule below only removes syntax it can identify from a
 * single line.
 */
export function markdownToPlainText(markdown: string): string {
  const lines = markdown.split('\n');
  const out: string[] = [];
  let inFence = false;

  for (const line of lines) {
    if (/^ {0,3}(`{3,}|~{3,})/.test(line)) {
      inFence = !inFence;
      continue;
    }

    // Fenced content is already literal; unwrapping inline syntax inside it
    // would corrupt whatever the block was quoting.
    out.push(inFence ? line : formatLine(line));
  }

  return out
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

function formatLine(line: string): string {
  let text = line;

  text = text.replace(/^ {0,3}#{1,6}\s+/, '');
  text = text.replace(/^ {0,3}>\s?/, '');
  // A thematic break has no plain-text spelling that is not noise.
  text = /^ {0,3}([-*_])(\s*\1){2,}\s*$/.test(text) ? '' : text;
  text = text.replace(/^(\s*)[*+]\s+/, '$1- ');

  return formatInline(text);
}

function formatInline(text: string): string {
  return (
    text
      // Images before links: the link rule would otherwise eat the `!` alt text
      // and leave a bare `!` behind.
      .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
      // A link whose label already is its target reads as one thing, not two.
      .replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, label: string, url: string) =>
        label.trim() === url.trim() ? label : `${label} (${url})`,
      )
      .replace(/`([^`]+)`/g, '$1')
      .replace(/\*\*(?=\S)(.+?)(?<=\S)\*\*/g, '$1')
      .replace(/\*(?=\S)([^*]+?)(?<=\S)\*/g, '$1')
      // Underscore emphasis cannot open or close inside a word, or
      // `a_variable_name` comes out as `avariablename`.
      .replace(/(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)/g, '$1')
      .replace(/(?<!\w)_(?=\S)([^_]+?)(?<=\S)_(?!\w)/g, '$1')
      .replace(/~~(?=\S)(.+?)(?<=\S)~~/g, '$1')
  );
}
