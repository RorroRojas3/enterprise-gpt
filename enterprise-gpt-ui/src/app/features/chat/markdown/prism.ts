import Prism from 'prismjs';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-python';
import 'prismjs/components/prism-bash';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-yaml';
import 'prismjs/components/prism-sql';

// A deliberate floor of grammars: each costs chat-chunk bytes, and an unlisted language renders
// unhighlighted. Core already carries markup, css, clike and javascript.
// Imported as a value so the statements below never depend on side-effect import order, which
// differs between the build and the specs.

// Otherwise Prism queues a `highlightAll()` over the whole shell on load; the queued callback
// re-reads this flag, so setting it synchronously cancels that pass.
Prism.manual = true;

// `ngx-markdown` reads the highlighter as a global, and the specs' global is not the one Prism
// wrote to.
(globalThis as typeof globalThis & { Prism?: typeof Prism }).Prism = Prism;
