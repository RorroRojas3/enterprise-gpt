import Prism from 'prismjs';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-python';
import 'prismjs/components/prism-bash';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-yaml';
import 'prismjs/components/prism-sql';

// Prism and the grammars the transcript highlights (US-601, US-604).
//
// The grammar list is a deliberate floor, not a catalogue: each one is bytes in
// the chat chunk, and an unlisted language still renders as an unhighlighted code
// block rather than as an error. Prism's core already carries markup, css, clike
// and javascript, so only these seven are added — the languages this platform's
// users actually paste and the model actually answers in. `csharp` and
// `typescript` extend `clike` and `javascript`, which core supplies, so the set is
// self-contained.
//
// Prism is imported as a value rather than for a side effect alone. That is what
// makes the two statements below depend on the module itself instead of on the
// order side-effect imports happen to be evaluated in — an order that differs
// between the application build and the specs, and silently cost the transcript
// its highlighting when this file relied on it.

// Left to itself Prism queues a `highlightAll()` over the whole document as soon
// as it loads, which here would walk the signed-in shell outside Angular. The
// queued callback re-reads this flag when it fires, and module evaluation is
// synchronous, so setting it on the next line cancels that pass before any frame
// can run it.
Prism.manual = true;

// `ngx-markdown` looks the highlighter up as a bare global rather than importing
// it. Prism publishes itself there already in a browser; assigning it explicitly
// costs nothing and keeps the specs, whose global object is not the same one
// Prism wrote to, highlighting the same way the application does.
(globalThis as typeof globalThis & { Prism?: typeof Prism }).Prism = Prism;
