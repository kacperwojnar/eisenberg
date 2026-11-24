"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type Organization = { id: string; name: string; settingsText: string };

export default function OrganizationsPage() {
  const [rows, setRows] = useState<Organization[]>([]);
  const [name, setName] = useState("");
  const [selected, setSelected] = useState("");
  const [settings, setSettings] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function load() {
    const next = await api<Organization[]>("/organizations");
    setRows(next);
    setSelected((current) => current || next[0]?.id || "");
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    setSettings(rows.find((row) => row.id === selected)?.settingsText ?? "");
  }, [selected, rows]);

  async function create(event: FormEvent) {
    event.preventDefault();
    setError("");
    await api("/organizations", { method: "POST", body: JSON.stringify({ name }) });
    setName("");
    setNotice("Organization created.");
    await load();
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    setError("");
    await api(`/organizations/${selected}/settings`, { method: "PUT", body: JSON.stringify({ text: settings }) });
    setNotice("Settings replaced.");
    await load();
  }

  return (
    <main>
      <h1>Organizations</h1>
      {error && <p>{error}</p>}
      {notice && <p className="note">{notice}</p>}
      <form onSubmit={create}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" required />
        <button type="submit">Create</button>
      </form>
      <table>
        <thead><tr><th>Name</th><th /></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{row.name}</td>
              <td><button type="button" onClick={() => setSelected(row.id)}>Settings</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {selected && (
        <form className="stack" onSubmit={save}>
          <h2>{rows.find((row) => row.id === selected)?.name}</h2>
          <textarea value={settings} onChange={(e) => setSettings(e.target.value)} rows={5} />
          <button type="submit">Replace settings</button>
        </form>
      )}
    </main>
  );
}
