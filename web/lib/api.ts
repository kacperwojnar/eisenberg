export type DevRole = "admin" | "user";

export function devRole(): DevRole {
  if (typeof window === "undefined") return "user";
  return localStorage.getItem("eisenberg-dev-user") === "admin" ? "admin" : "user";
}

export function setDevRole(role: DevRole) {
  localStorage.setItem("eisenberg-dev-user", role);
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("X-Dev-User", devRole());
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type"))
    headers.set("Content-Type", "application/json");
  const response = await fetch("http://localhost:5000" + path, { ...init, headers });
  if (!response.ok) throw new Error(await response.text());
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
