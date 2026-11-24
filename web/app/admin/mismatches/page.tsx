"use client";

import Link from "next/link";
import { FormEvent, Fragment, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type Organization = { id: string; name: string; settingsText: string };
type Mismatch = { id: string; organizationId: string; organizationName: string; userEmail: string; createdAt: string; text: string; handled: boolean };
type FormChange = { id: string; formId: string; address: string; organizationName: string; userEmail: string; createdAt: string; text: string; handled: boolean };

export default function MismatchesPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [rows, setRows] = useState<Mismatch[]>([]);
  const [changes, setChanges] = useState<FormChange[]>([]);
  const [openId, setOpenId] = useState("");
  const [organizationId, setOrganizationId] = useState("");
  const [text, setText] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  async function load() {
    const next = await api<Organization[]>("/organizations");
    const reports = await api<Mismatch[]>("/admin/mismatches");
    setOrganizations(next);
    setRows(reports);
    setChanges(await api<FormChange[]>("/admin/form-changes"));
    const requested = new URLSearchParams(window.location.search).get("organization") || "";
    setOrganizationId((current) => requested || current || next[0]?.id || "");
    const match = reports.find((row) => row.organizationId === requested);
    if (match) setOpenId(match.id);
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    const organization = organizations.find((item) => item.id === organizationId);
    setText(organization?.settingsText ?? "");
  }, [organizationId, organizations]);

  function openSettings(row: Mismatch) {
    setOpenId(row.id);
    setOrganizationId(row.organizationId);
    setNotice("");
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    await api(`/organizations/${organizationId}/settings`, {
      method: "PUT",
      body: JSON.stringify({ text })
    });
    setNotice("Settings replaced.");
    await load();
  }

  async function startRun(id: string) {
    setError("");
    try {
      await api(`/admin/form-changes/${id}/run`, { method: "POST" });
      setNotice("A new run is queued.");
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const selected = organizations.find((item) => item.id === organizationId);

  return (
    <main className="wide">
      <h1>Reports</h1>
      {error && <p className="failed">{error}</p>}
      {notice && <p className="note">{notice}</p>}
      <section>
        <h2>Settings</h2>
        <p className="note">A user reported that something does not match the organization settings. The settings text opens beside the report.</p>
        <table>
          <thead><tr><th>When</th><th>Organization</th><th>User</th><th>Text</th><th>Status</th><th></th></tr></thead>
          <tbody>
            {rows.map((row) => (
              <Fragment key={row.id}>
                <tr>
                  <td>{new Date(row.createdAt).toLocaleString()}</td>
                  <td>{row.organizationName}</td>
                  <td>{row.userEmail}</td>
                  <td>{row.text}</td>
                  <td>{row.handled ? "Settings replaced" : "Open"}</td>
                  <td><button type="button" onClick={() => openSettings(row)}>Open settings</button></td>
                </tr>
                {openId === row.id && (
                  <tr key={`${row.id}-settings`}>
                    <td colSpan={6}>
                      <form className="stack" onSubmit={save}>
                        <strong>Settings for {selected?.name || row.organizationName}</strong>
                        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={6} />
                        <button type="submit">Replace settings</button>
                      </form>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
            {rows.length === 0 && <tr><td colSpan={6}>No settings reports.</td></tr>}
          </tbody>
        </table>
      </section>
      <section>
        <h2>Form changes</h2>
        <p className="note">The extension reported that the open page no longer matches the published model. Starting a run interprets that form again.</p>
        <table>
          <thead><tr><th>When</th><th>Form</th><th>Organization</th><th>User</th><th>What differed</th><th></th></tr></thead>
          <tbody>
            {changes.map((item) => (
              <tr key={item.id}>
                <td>{new Date(item.createdAt).toLocaleString()}</td>
                <td><Link href={`/admin/forms/${item.formId}`}>{item.address}</Link></td>
                <td>{item.organizationName}</td>
                <td>{item.userEmail}</td>
                <td>{item.text}</td>
                <td>{item.handled ? "Run started" : <button type="button" onClick={() => startRun(item.id)}>Start a new run</button>}</td>
              </tr>
            ))}
            {changes.length === 0 && <tr><td colSpan={6}>No form-change reports.</td></tr>}
          </tbody>
        </table>
      </section>
    </main>
  );
}
