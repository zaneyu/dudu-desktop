/** Shared JSON request-body parsing for every route that accepts a body. */

export class UnsupportedMediaTypeError extends Error {}

/**
 * Parses a request body as JSON. Throws `UnsupportedMediaTypeError` when the `Content-Type`
 * header is not `application/json` (callers map that to 415); any other failure (body is not
 * valid JSON) propagates as a plain error, which callers map to a generic 400 — there is no
 * behavioural difference between "malformed" and any other parse failure, so a single plain-400
 * path suffices rather than a dedicated error class.
 */
export async function readJsonBody(request: Request): Promise<unknown> {
  const contentType = request.headers.get("Content-Type") ?? "";
  if (!contentType.toLowerCase().includes("application/json")) {
    throw new UnsupportedMediaTypeError();
  }
  return request.json();
}
