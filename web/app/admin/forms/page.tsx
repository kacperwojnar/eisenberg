"use client";

import Link from "next/link";
import { FormEvent, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type FormRow = {
  id: string;
  address: string;
  description: string;
  createdAt: string;
  publishedModelNumber: number | null;
  lastRun: { id: string; status: string; statusText: string; createdAt: string } | null;
};

export default function FormsPage() {
  const [rows, setRows] = useState<FormRow[]>([]);
  const [address, setAddress] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState("");

  async function load() {
    setRows(await api<FormRow[]>("/admin/forms"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  async function create(event: FormEvent) {
    event.preventDefault();
    setError("");
    try {
      const created = await api<{ id: string }>("/admin/forms", {
        method: "POST",
        body: JSON.stringify({ address, description })
      });
      window.location.href = `/admin/forms/${created.id}`;
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <main>
      <h1>Forms</h1>
      {error && <p>{error}</p>}
      <form className="stack" onSubmit={create}>
        <input value={address} onChange={(e) => setAddress(e.target.value)} placeholder="Address of the form" required />
        <textarea
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={5}
          placeholder="What the form is for, how to reach it after signing in, what must not be clicked, and any test values"
        />
        <div className="row">
          <button type="submit">Add and interpret</button>
          <span className="note">The interpretation runs in the background.</span>
        </div>
      </form>
      <table>
        <thead><tr><th>Address</th><th>Last run</th><th>Published</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td><Link href={`/admin/forms/${row.id}`}>{row.address}</Link></td>
              <td>{row.lastRun ? `${row.lastRun.status}: ${row.lastRun.statusText}` : "none"}</td>
              <td>{row.publishedModelNumber ? `model ${row.publishedModelNumber}` : "no"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </main>
  );
}
