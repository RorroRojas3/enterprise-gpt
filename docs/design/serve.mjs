// Static server for the Claude Design handoff bundle in ./project.
//
// The boards cannot be opened from disk: support.js resolves every
// <dc-import name="Sidebar"> by fetch()-ing the sibling .dc.html file, and a
// file:// document is an opaque origin, so every one of those fetches is
// rejected by CORS and the components render as empty placeholders. Serving
// the bundle over http:// is the whole fix -- no file in ./project is touched.
//
//   node docs/design/serve.mjs [--port 4300]

import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join, relative, resolve, extname, dirname, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), 'project');
const INDEX = '/export-src.html';
const HOST = '127.0.0.1';

const portFlag = process.argv.indexOf('--port');
const PORT = Number(
  (portFlag !== -1 ? process.argv[portFlag + 1] : undefined) ?? process.env['PORT'] ?? 4300,
);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

function send(res, status, body, headers = {}) {
  res.writeHead(status, { 'Cache-Control': 'no-store', ...headers });
  res.end(body);
}

const server = createServer(async (req, res) => {
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    return send(res, 405, 'Method not allowed\n', {
      'Content-Type': 'text/plain; charset=utf-8',
      Allow: 'GET, HEAD',
    });
  }

  const { pathname } = new URL(req.url ?? '/', `http://${HOST}:${PORT}`);
  if (pathname === '/') return send(res, 302, '', { Location: INDEX });

  let decoded;
  try {
    // Board filenames carry spaces -- "01 Chat and Streaming.dc.html".
    decoded = decodeURIComponent(pathname);
  } catch {
    return send(res, 400, 'Bad request\n', { 'Content-Type': 'text/plain; charset=utf-8' });
  }

  const filePath = resolve(ROOT, '.' + decoded);
  const rel = relative(ROOT, filePath);
  if (rel.startsWith('..') || isAbsolute(rel)) {
    return send(res, 403, 'Forbidden\n', { 'Content-Type': 'text/plain; charset=utf-8' });
  }

  let size;
  try {
    const info = await stat(filePath);
    if (!info.isFile()) throw new Error('not a file');
    size = info.size;
  } catch {
    return send(res, 404, `Not found: ${decoded}\n`, {
      'Content-Type': 'text/plain; charset=utf-8',
    });
  }

  const headers = {
    'Cache-Control': 'no-store',
    'Content-Type': MIME[extname(filePath).toLowerCase()] ?? 'application/octet-stream',
    'Content-Length': String(size),
  };
  res.writeHead(200, headers);
  if (req.method === 'HEAD') return res.end();
  createReadStream(filePath).pipe(res);
});

server.on('error', (err) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`Port ${PORT} is already in use. Pick another: node ${relative(process.cwd(), fileURLToPath(import.meta.url))} --port 4301`);
    process.exit(1);
  }
  throw err;
});

server.listen(PORT, HOST, () => {
  console.log(`Enterprise GPT design bundle -> http://localhost:${PORT}`);
  console.log(`Serving ${ROOT}`);
  console.log('React, Bootstrap and the fonts load from CDNs, so this needs an internet connection.');
});
