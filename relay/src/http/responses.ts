/**
 * Every JSON response (success or error) shares the same headers. Error bodies are always
 * `{ error: "<code>", message: "<short, generic text>" }` and never echo request bodies, tokens,
 * codes, or cookies back to the caller.
 */

function withStandardHeaders(headers?: HeadersInit): Headers {
  const result = new Headers(headers);
  result.set("Cache-Control", "no-store");
  return result;
}

export function jsonResponse(body: unknown, status = 200, headers?: HeadersInit): Response {
  const result = withStandardHeaders(headers);
  result.set("Content-Type", "application/json; charset=utf-8");
  return new Response(JSON.stringify(body), { status, headers: result });
}

export function errorResponse(
  status: number,
  error: string,
  message: string,
  headers?: HeadersInit,
): Response {
  return jsonResponse({ error, message }, status, headers);
}

export function noContentResponse(headers?: HeadersInit): Response {
  return new Response(null, { status: 204, headers: withStandardHeaders(headers) });
}

export const badRequest = (message = "The request body is malformed."): Response =>
  errorResponse(400, "bad_request", message);

export const unauthorized = (message = "Authentication is required."): Response =>
  errorResponse(401, "unauthorized", message);

export const forbidden = (message = "The Origin header did not match this server."): Response =>
  errorResponse(403, "forbidden", message);

export const notFound = (): Response =>
  errorResponse(404, "not_found", "No route matches this request.");

export const methodNotAllowed = (): Response =>
  errorResponse(405, "method_not_allowed", "This method is not supported for this route.");

export const gone = (message = "This pairing code is no longer valid."): Response =>
  errorResponse(410, "gone", message);

export const unsupportedMediaType = (message = "Requests must use application/json."): Response =>
  errorResponse(415, "unsupported_media_type", message);

export const tooManyRequests = (message = "Too many requests. Try again later."): Response =>
  errorResponse(429, "rate_limited", message);
