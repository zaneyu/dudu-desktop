/**
 * Device bearer credentials may travel over HTTPS, or over HTTP only when the request is truly
 * loopback for local development. A Worker cannot stop a client from attempting an unsafe HTTP
 * request before it arrives, but this fail-closed guard prevents the relay from accepting or
 * issuing device credentials on a non-loopback plaintext origin.
 */
export function isSecureTransport(request: Request): boolean {
  let url: URL;
  try {
    url = new URL(request.url);
  } catch {
    return false;
  }
  if (url.protocol === "https:") {
    return true;
  }
  return url.protocol === "http:" && isLoopbackHostname(url.hostname);
}

function isLoopbackHostname(hostname: string): boolean {
  return hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]" || hostname === "::1";
}
