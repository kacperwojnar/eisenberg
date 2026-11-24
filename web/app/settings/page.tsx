"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../lib/api";

type Me = { organizationIds: string[] };
type Organization = { id: string; name: string; settingsText: string };

export default function SettingsPage() {
  const [rows, setRows] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [text, setText] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  useEffect(() => {
    Promise.all([api<Me>("/me"), api<Organization[]>("/organizations")])
      .then(([me, organizations]) => {
        const mine = organizations.filter((organization) => me.organizationIds.includes(organization.id));
        setRows(mine);
        setOrganizationId(mine[0]?.id || "");
      })
      .catch((e: Error) => setError(e.message));
  }, []);

  async function report(event: FormEvent) {
    event.preventDefault();
    setError("");
    setNotice("");
    try {
      await api("/settings-mismatches", {
        method: "POST",
        body: JSON.stringify({ organizationId, text })
      });
      setText("");
      setNotice("Report sent. The admin replaces the settings text.");
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <main>
      <h1>Settings</h1>
      <p className="note">These are the organization settings. When something does not match, report what is missing. The admin replaces the text.</p>
      {error && <p className="failed">{error}</p>}
      {notice && <p className="note">{notice}</p>}
      {rows.map((organization) => (
        <section key={organization.id}>
          <h2>{organization.name}</h2>
          <p>{organization.settingsText || "No settings text."}</p>
        </section>
      ))}
      {rows.length > 0 && (
        <form className="stack" onSubmit={report}>
          <h2>Report a mismatch</h2>
          <select value={organizationId} onChange={(e) => setOrganizationId(e.target.value)}>
            {rows.map((organization) => (
              <option key={organization.id} value={organization.id}>{organization.name}</option>
            ))}
          </select>
          <textarea value={text} onChange={(e) => setText(e.target.value)} rows={5} required placeholder="What does not match, and what information is required" />
          <button type="submit">Send report</button>
        </form>
      )}
    </main>
  );
}
