import { NextRequest } from "next/server";

export const dynamic = "force-dynamic";

const forwarded = ["authorization", "x-dev-user", "content-type", "accept"];

async function proxy(request: NextRequest, path: string[]) {
  if (path.some((part) => part === ".." || part.includes("\\") || part.includes(":")))
    return new Response("Bad path.", { status: 400 });
  const base = (process.env.API_BASE_URL || "http://localhost:5000").replace(/\/$/, "");
  const url = new URL(`${base}/${path.join("/")}`);
  url.search = request.nextUrl.search;
  const headers = new Headers();
  for (const name of forwarded) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  const hasBody = request.method !== "GET" && request.method !== "HEAD";
  const response = await fetch(url, {
    method: request.method,
    headers,
    body: hasBody ? await request.arrayBuffer() : undefined,
    redirect: "manual"
  });
  const out = new Headers();
  const type = response.headers.get("content-type");
  if (type) out.set("content-type", type);
  return new Response(response.status === 204 ? null : await response.arrayBuffer(), {
    status: response.status,
    headers: out
  });
}

type Context = { params: Promise<{ path: string[] }> };

export async function GET(request: NextRequest, context: Context) {
  return proxy(request, (await context.params).path);
}

export async function POST(request: NextRequest, context: Context) {
  return proxy(request, (await context.params).path);
}

export async function PUT(request: NextRequest, context: Context) {
  return proxy(request, (await context.params).path);
}

export async function DELETE(request: NextRequest, context: Context) {
  return proxy(request, (await context.params).path);
}
