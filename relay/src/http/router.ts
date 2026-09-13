import type { Env } from "../env.js";
import { methodNotAllowed, notFound } from "./responses.js";

export type RouteHandler = (request: Request, env: Env, ctx: ExecutionContext) => Promise<Response>;

export interface RegisteredRoute {
  method: string;
  path: string;
}

/**
 * A small exact-path, method-keyed router: no frameworks, no path parameters (every route this
 * Worker serves is a static path). `allRegisteredRoutes()` lets tests (this task's and Task 18's)
 * enumerate what is wired up without duplicating the list by hand.
 */
export class Router {
  readonly #routes: Array<RegisteredRoute & { handler: RouteHandler }> = [];

  add(method: string, path: string, handler: RouteHandler): void {
    this.#routes.push({ method, path, handler });
  }

  allRegisteredRoutes(): RegisteredRoute[] {
    return this.#routes.map(({ method, path }) => ({ method, path }));
  }

  async handle(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const { pathname } = new URL(request.url);
    const matchingPath = this.#routes.filter((route) => route.path === pathname);
    if (matchingPath.length === 0) {
      return notFound();
    }
    const matchingMethod = matchingPath.find((route) => route.method === request.method);
    if (!matchingMethod) {
      return methodNotAllowed();
    }
    return matchingMethod.handler(request, env, ctx);
  }
}
