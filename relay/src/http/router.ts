import type { Env } from "../env.js";
import { internalError, methodNotAllowed, notFound } from "./responses.js";

export type RouteHandler = (
  request: Request,
  env: Env,
  ctx: ExecutionContext,
  params: Record<string, string>,
) => Promise<Response>;

export interface RegisteredRoute {
  method: string;
  path: string;
}

/**
 * A small path-segment router: every route is either a static segment or a `:param` segment
 * (e.g. `/v1/messages/:id/ack`), matched by segment count and exact-vs-`:param` per segment — no
 * regex, no wildcards, no optional segments. Exact-match routes (every route before Task 18) are
 * unaffected: a path with no `:` segments only ever matches itself. `allRegisteredRoutes()` lets
 * tests enumerate what is wired up without duplicating the list by hand.
 */
export class Router {
  readonly #routes: Array<RegisteredRoute & { handler: RouteHandler; segments: string[] }> = [];

  add(method: string, path: string, handler: RouteHandler): void {
    this.#routes.push({ method, path, handler, segments: path.split("/") });
  }

  allRegisteredRoutes(): RegisteredRoute[] {
    return this.#routes.map(({ method, path }) => ({ method, path }));
  }

  async handle(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const { pathname } = new URL(request.url);
    const requestSegments = pathname.split("/");
    const matchingPath = this.#routes.filter((route) => matchesSegments(route.segments, requestSegments));
    if (matchingPath.length === 0) {
      return notFound();
    }
    const matchingMethod = matchingPath.find((route) => route.method === request.method);
    if (!matchingMethod) {
      return methodNotAllowed();
    }
    const params = extractParams(matchingMethod.segments, requestSegments);
    try {
      return await matchingMethod.handler(request, env, ctx, params);
    } catch {
      // Deliberately logs no detail: a handler's thrown error could, in principle, carry request
      // context, and no log line may contain envelope fields, tokens, or cookies.
      console.error("Unhandled error while handling a request.");
      return internalError();
    }
  }
}

function matchesSegments(patternSegments: string[], requestSegments: string[]): boolean {
  if (patternSegments.length !== requestSegments.length) {
    return false;
  }
  return patternSegments.every((segment, index) => segment.startsWith(":") || segment === requestSegments[index]);
}

function extractParams(patternSegments: string[], requestSegments: string[]): Record<string, string> {
  const params: Record<string, string> = {};
  for (const [index, segment] of patternSegments.entries()) {
    if (segment.startsWith(":")) {
      params[segment.slice(1)] = decodeURIComponent(requestSegments[index]);
    }
  }
  return params;
}
