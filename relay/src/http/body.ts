/**
 * Shared JSON request-body parsing for every route that accepts a body. Hardened per Task 21:
 * the raw byte stream is capped (and the read cancelled early) before `JSON.parse` ever runs, and
 * the raw text is scanned for prototype-pollution keys and duplicate keys that `JSON.parse` would
 * otherwise silently collapse to its last occurrence.
 */

export class UnsupportedMediaTypeError extends Error {}

/** The raw body exceeded `MAXIMUM_BODY_BYTES`. The read is cancelled as soon as this is known,
 * without ever buffering the rest of the stream. Maps to 413. */
export class JsonBodyTooLargeError extends Error {}

/** The raw JSON text carried a `__proto__`/`constructor`/`prototype` key, or the same key twice
 * in one object. Maps to 400: this is a malformed-request concern, not an envelope-semantics
 * one, so it shares `UnsupportedMediaTypeError`'s sibling generic-400 treatment. */
export class JsonBodyUnsafeError extends Error {}

/**
 * No legitimate request body this relay accepts is anywhere close to this size (the largest,
 * `POST /v1/messages`, is bounded far below it by its own ciphertext/key limits once parsed) —
 * this cap exists purely so a hostile, oversized body is rejected by byte count alone, before a
 * single byte is handed to `JSON.parse`.
 */
const MAXIMUM_BODY_BYTES = 16 * 1024;

const FORBIDDEN_KEYS = new Set(["__proto__", "constructor", "prototype"]);

/**
 * Parses a request body as JSON. Throws `UnsupportedMediaTypeError` when the `Content-Type`
 * header is not `application/json` (callers map that to 415); `JsonBodyTooLargeError` when the
 * raw body exceeds `MAXIMUM_BODY_BYTES` (callers map that to 413); `JsonBodyUnsafeError` when the
 * raw text carries a forbidden or duplicate key (callers map that to 400); any other failure
 * (body is not valid JSON) propagates as a plain `SyntaxError`, which callers also map to a
 * generic 400.
 */
export async function readJsonBody(request: Request): Promise<unknown> {
  const contentType = request.headers.get("Content-Type") ?? "";
  if (!contentType.toLowerCase().includes("application/json")) {
    throw new UnsupportedMediaTypeError();
  }

  const text = await readBoundedText(request, MAXIMUM_BODY_BYTES);
  rejectUnsafeJson(text);
  return JSON.parse(text);
}

/**
 * Streams the request body through its `ReadableStream` reader, counting bytes as they arrive so
 * an oversized body is caught and the read cancelled (never fully buffered) as soon as the cap is
 * crossed. Deliberately Workers-native (no Node `Buffer`/stream APIs): this repo omits
 * `nodejs_compat` on purpose.
 */
async function readBoundedText(request: Request, maxBytes: number): Promise<string> {
  const body = request.body;
  if (!body) {
    return "";
  }

  const reader = body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel();
      throw new JsonBodyTooLargeError();
    }
    chunks.push(value);
  }

  const combined = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    combined.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(combined);
}

/**
 * Scans raw JSON text for two things `JSON.parse` would otherwise hide: a key appearing twice in
 * the same object (the native parser just keeps the last occurrence) and a `__proto__` /
 * `constructor` / `prototype` key at any nesting depth (a classic prototype-pollution vector,
 * even though this relay's own `JSON.parse` result is always read through typed field access
 * rather than assigned onto a live object's prototype — defense in depth for every current and
 * future caller of `readJsonBody`).
 *
 * Walks the text once, character by character, tracking only enough structure to tell a key
 * string (immediately followed by `:`) from a value string (followed by `,`/`}`/`]`) and to know
 * which currently-open brace is an object (whose keys get tracked) versus an array (which has
 * none). Values themselves are never interpreted — only skipped past — so this has no opinion on
 * numbers, `true`/`false`/`null`, or nested content beyond finding its matching close.
 */
function rejectUnsafeJson(text: string): void {
  const keysPerObject: Array<Set<string>> = [];
  const kindStack: Array<"object" | "array"> = [];
  let i = 0;
  const length = text.length;

  while (i < length) {
    const ch = text[i];

    if (ch === '"') {
      const stringStart = i;
      i = scanStringLiteral(text, i);
      const stringEnd = i;
      while (i < length && /\s/.test(text[i])) {
        i++;
      }
      const isKeyPosition = kindStack[kindStack.length - 1] === "object" && text[i] === ":";
      if (isKeyPosition) {
        const key = JSON.parse(text.slice(stringStart, stringEnd)) as string;
        if (FORBIDDEN_KEYS.has(key)) {
          throw new JsonBodyUnsafeError(`The key '${key}' is not allowed.`);
        }
        const currentKeys = keysPerObject[keysPerObject.length - 1];
        if (currentKeys.has(key)) {
          throw new JsonBodyUnsafeError("Duplicate key in request body.");
        }
        currentKeys.add(key);
      }
      continue;
    }

    if (ch === "{") {
      keysPerObject.push(new Set());
      kindStack.push("object");
      i++;
      continue;
    }

    if (ch === "[") {
      kindStack.push("array");
      i++;
      continue;
    }

    if (ch === "}") {
      if (kindStack[kindStack.length - 1] === "object") {
        keysPerObject.pop();
      }
      kindStack.pop();
      i++;
      continue;
    }

    if (ch === "]") {
      kindStack.pop();
      i++;
      continue;
    }

    i++;
  }
}

/** Returns the index just past the closing quote of the string literal starting at `start`
 * (`text[start]` must be `"`), respecting backslash escapes. Never decodes the string itself. */
function scanStringLiteral(text: string, start: number): number {
  let i = start + 1;
  const length = text.length;
  while (i < length) {
    const ch = text[i];
    if (ch === "\\") {
      i += 2;
      continue;
    }
    if (ch === '"') {
      return i + 1;
    }
    i++;
  }
  throw new JsonBodyUnsafeError("Unterminated string in request body.");
}
