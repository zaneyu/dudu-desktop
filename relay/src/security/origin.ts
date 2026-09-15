/**
 * Cookie-authenticated mutations (pairing redemption, sender disconnect, and Task 18's message
 * submit) must carry an `Origin` header that matches the request's own origin, as a CSRF
 * defence: a cookie alone is not sufficient authorization for those routes.
 */
export function isSameOrigin(request: Request): boolean {
  const origin = request.headers.get("Origin");
  if (!origin) {
    return false;
  }
  return originMatches(origin, request);
}

/**
 * Compares an `Origin` value to the request's own origin after URL normalization (host case,
 * default ports), so an equivalent spelling is not rejected. The value must still be a bare
 * origin: the opaque `"null"`, anything unparseable, or anything carrying a path, query, or
 * credentials never matches.
 */
function originMatches(origin: string, request: Request): boolean {
  try {
    const parsed = new URL(origin);
    const isBareOrigin =
      parsed.pathname === "/" && !parsed.search && !parsed.hash && !parsed.username && !parsed.password;
    return isBareOrigin && parsed.origin !== "null" && parsed.origin === new URL(request.url).origin;
  } catch {
    return false;
  }
}

/**
 * Best-effort CSRF assert for the cookie-authenticated GETs (`GET /v1/sender/device`,
 * `GET /v1/messages/:id/status`), which cannot require `Origin` outright: browser navigations
 * and some same-origin fetches omit it. When the browser sends `Sec-Fetch-Site` it decides
 * (`same-origin`/`none` allowed, `same-site`/`cross-site` denied). Otherwise a present `Origin`
 * must match (same rule as mutations), else a present `Referer` must be same-origin. Requests carrying
 * neither header are still allowed — documented test note: those GETs are read-only (they
 * change no state and return only the caller's own session-scoped data), the session cookie is
 * `__Host`-prefixed + `SameSite=Strict` (so cross-site sends are already suppressed at the
 * browser layer), and every state-changing cookie route keeps the strict `isSameOrigin` gate.
 * A present-but-mismatched header fails closed in both branches.
 */
export function isSafeCookieGet(request: Request): boolean {
  // Fetch Metadata is set by the browser and cannot be forged by page script; when present it is
  // the most precise signal, and it covers same-origin requests whose Origin is absent or "null".
  const fetchSite = request.headers.get("Sec-Fetch-Site");
  if (fetchSite !== null) {
    return fetchSite === "same-origin" || fetchSite === "none";
  }
  const origin = request.headers.get("Origin");
  if (origin !== null) {
    return originMatches(origin, request);
  }
  const referer = request.headers.get("Referer");
  if (referer !== null) {
    try {
      return new URL(referer).origin === new URL(request.url).origin;
    } catch {
      return false;
    }
  }
  return true;
}
