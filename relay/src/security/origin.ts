/**
 * Cookie-authenticated mutations (pairing redemption, sender disconnect, and Task 18's message
 * submit) must carry an `Origin` header that matches the request's own origin exactly, as a CSRF
 * defence: a cookie alone is not sufficient authorization for those routes.
 */
export function isSameOrigin(request: Request): boolean {
  const origin = request.headers.get("Origin");
  if (!origin) {
    return false;
  }
  try {
    return origin === new URL(request.url).origin;
  } catch {
    return false;
  }
}
