/** The sender-session cookie: a `__Host-` prefixed, HttpOnly, Secure, SameSite=Strict cookie. */
export const SENDER_COOKIE_NAME = "__Host-dudu_sender";

/** Sender sessions are valid for 180 days from creation. */
export const SENDER_SESSION_MAX_AGE_SECONDS = 180 * 24 * 60 * 60;

/** `Set-Cookie` value that establishes a new sender session. Never log this value. */
export function buildSenderSessionCookie(token: string): string {
  return `${SENDER_COOKIE_NAME}=${token}; Path=/; HttpOnly; Secure; SameSite=Strict; `
    + `Max-Age=${SENDER_SESSION_MAX_AGE_SECONDS}`;
}

/** `Set-Cookie` value that clears the sender-session cookie on disconnect. */
export function buildClearedSenderSessionCookie(): string {
  return `${SENDER_COOKIE_NAME}=; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=0`;
}

/** Reads the sender-session token from the request's `Cookie` header, if present. */
export function readSenderSessionToken(request: Request): string | null {
  const header = request.headers.get("Cookie");
  if (!header) {
    return null;
  }
  for (const part of header.split(";")) {
    const separatorIndex = part.indexOf("=");
    if (separatorIndex === -1) {
      continue;
    }
    const name = part.slice(0, separatorIndex).trim();
    if (name === SENDER_COOKIE_NAME) {
      const value = part.slice(separatorIndex + 1).trim();
      return value.length > 0 ? value : null;
    }
  }
  return null;
}
