export type DevRole = "admin" | "user";

export function devRole(): DevRole {
  if (typeof window === "undefined") return "user";
  return localStorage.getItem("eisenberg-dev-user") === "admin" ? "admin" : "user";
}

export function setDevRole(role: DevRole) {
  localStorage.setItem("eisenberg-dev-user", role);
}

function apiUrl(path: string) {
  return `/backend${path}`;
}

export async function apiBlobUrl(path: string): Promise<string> {
  const response = await fetch(apiUrl(path), { headers: headers() });
  if (!response.ok) throw new Error(await response.text());
  return URL.createObjectURL(await response.blob());
}

function headers(): Record<string, string> {
  const token = typeof window === "undefined" ? "" : sessionStorage.getItem("eisenberg-token") || "";
  return token ? { Authorization: `Bearer ${token}` } : { "X-Dev-User": devRole() };
}

export type FieldProblem = { property: string | null; message: string };

export class ApiError extends Error {
  problems: FieldProblem[];

  constructor(message: string, problems: FieldProblem[] = []) {
    super(message);
    this.problems = problems;
  }
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const next = new Headers(init.headers);
  const current = headers();
  for (const [key, value] of Object.entries(current)) next.set(key, value);
  if (init.body && !(init.body instanceof FormData) && !next.has("Content-Type"))
    next.set("Content-Type", "application/json");
  const response = await fetch(apiUrl(path), { ...init, headers: next });
  if (!response.ok) {
    const text = await response.text();
    try {
      const body = JSON.parse(text) as { problems?: FieldProblem[] };
      if (Array.isArray(body.problems))
        throw new ApiError(body.problems.map((problem) => problem.message).join(" "), body.problems);
    } catch (error) {
      if (error instanceof ApiError) throw error;
    }
    throw new ApiError(text);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
