"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type Organization = { id: string; name: string; settingsText: string };

export default function OrganizationsPage() {
  const [rows, setRows] = useState<Organization[]>([]);
  const [name, setName] = useState("");
  const [error, setError] = useState("");

  async function load() {
    setRows(await api<Organization[]>("/organizations"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  async function create(event: FormEvent) {
    event.preventDefault();
    await api("/organizations", { method: "POST", body: JSON.stringify({ name }) });
    setName("");
    await load();
  }

  return (
    <main>
      <h1>Organizations</h1>
      {error && <p>{error}</p>}
      <form onSubmit={create}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" required />
        <button type="submit">Create</button>
      </form>
      <table>
        <thead><tr><th>Name</th></tr></thead>
        <tbody>
          {rows.map((row) => <tr key={row.id}><td>{row.name}</td></tr>)}
        </tbody>
      </table>
    </main>
  );
}
