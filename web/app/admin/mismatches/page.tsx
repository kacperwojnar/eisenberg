"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type Organization = { id: string; name: string; settingsText: string };
type Mismatch = { id: string; organizationId: string; createdAt: string; text: string };

export default function MismatchesPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [rows, setRows] = useState<Mismatch[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [text, setText] = useState("");
  const [error, setError] = useState("");

  async function load() {
    const next = await api<Organization[]>("/organizations");
    setOrganizations(next);
    setOrganizationId((current) => current || next[0]?.id || "");
    setRows(await api<Mismatch[]>("/admin/mismatches"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    const organization = organizations.find((item) => item.id === organizationId);
    setText(organization?.settingsText ?? "");
  }, [organizationId, organizations]);

  async function save(event: FormEvent) {
    event.preventDefault();
    await api(`/organizations/${organizationId}/settings`, {
      method: "PUT",
      body: JSON.stringify({ text })
    });
    await load();
  }

  return (
    <main>
      <h1>Settings mismatches</h1>
      {error && <p>{error}</p>}
      <table>
        <thead><tr><th>When</th><th>Text</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}><td>{new Date(row.createdAt).toLocaleString()}</td><td>{row.text}</td></tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={save}>
        <select value={organizationId} onChange={(e) => setOrganizationId(e.target.value)}>
          {organizations.map((organization) => (
            <option key={organization.id} value={organization.id}>{organization.name}</option>
          ))}
        </select>
        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} />
        <button type="submit">Replace settings</button>
      </form>
    </main>
  );
}
