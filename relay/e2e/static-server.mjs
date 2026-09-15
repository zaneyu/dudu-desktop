// A minimal static file server for Playwright's webServer, serving `relay/public` (including its
// `dist/` build output). No new dependency: plain Node `http`. Not a stand-in for the Worker's
// `_headers`-driven response headers, which only apply when Wrangler serves the same directory.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const publicDir = path.join(here, "..", "public");

const MIME_TYPES = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".txt": "text/plain; charset=utf-8",
};

const port = process.env.PORT ? Number(process.env.PORT) : 4173;

const server = createServer((request, response) => {
  void (async () => {
    try {
      const url = new URL(request.url ?? "/", "http://localhost");
      let pathname = decodeURIComponent(url.pathname);
      if (pathname === "/") {
        pathname = "/index.html";
      }
      const filePath = path.join(publicDir, pathname);
      if (!filePath.startsWith(publicDir)) {
        response.writeHead(403);
        response.end();
        return;
      }
      const body = await readFile(filePath);
      const contentType = MIME_TYPES[path.extname(filePath)] ?? "application/octet-stream";
      response.writeHead(200, {
        "Content-Type": contentType,
        "Cache-Control": "no-store",
        "Pragma": "no-cache",
        "X-Content-Type-Options": "nosniff",
        "Referrer-Policy": "no-referrer",
      });
      response.end(body);
    } catch {
      response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
      response.end("not found");
    }
  })();
});

server.listen(port, () => {
  console.log(`static server listening on http://localhost:${port}`);
});
