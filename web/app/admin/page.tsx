"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api } from "../../lib/api";

type Waiting = { formId: string; address: string; runId: string; liveViewUrl: string | null; statusText: string };
type Failed = { formId: string; address: string; runId: string; status: string; statusText: string };
type SettingsReport = { id: string; organizationId: string; organizationName: string; userEmail: string; createdAt: string; text: string };
type FormChange = { id: string; formId: string; address: string; organizationName: string; userEmail: string; createdAt: string; text: string };
type Attention = { waiting: Waiting[]; failed: Failed[]; settings: SettingsReport[]; formChanges: FormChange[] };

export default function AdminHome() {
  const [attention, setAttention] = useState<Attention | null>(null);
  const [error, setError] = useState("");

  async function load() {
    setAttention(await api<Attention>("/admin/attention"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  async function startRun(id: string) {
    setError("");
    try {
      await api(`/admin/form-changes/${id}/run`, { method: "POST" });
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <main className="wide">
      <h1>Admin</h1>
      <p className="note">What is waiting: a run that needs a sign-in, a run that failed, a settings report, or a form whose open page no longer matches the model.</p>
      {error && <p className="failed">{error}</p>}
      <section>
        <h2>Waiting for sign-in</h2>
        <table>
          <thead><tr><th>Form</th><th>Status</th><th>Live view</th></tr></thead>
          <tbody>
            {attention?.waiting.map((item) => (
              <tr key={item.runId}>
                <td><Link href={`/admin/forms/${item.formId}`}>{item.address}</Link></td>
                <td>{item.statusText}</td>
                <td>{item.liveViewUrl ? <a href={item.liveViewUrl} target="_blank" rel="noreferrer">Open the live view</a> : "No link yet"}</td>
              </tr>
            ))}
            {attention && attention.waiting.length === 0 && <tr><td colSpan={3}>None.</td></tr>}
          </tbody>
        </table>
      </section>
      <section>
        <h2>Failed runs</h2>
        <table>
          <thead><tr><th>Form</th><th>Status</th></tr></thead>
          <tbody>
            {attention?.failed.map((item) => (
              <tr key={item.runId}>
                <td><Link href={`/admin/forms/${item.formId}`}>{item.address}</Link></td>
                <td>{item.status}: {item.statusText}</td>
              </tr>
            ))}
            {attention && attention.failed.length === 0 && <tr><td colSpan={2}>None.</td></tr>}
          </tbody>
        </table>
      </section>
      <section>
        <h2>Forms that no longer match</h2>
        <table>
          <thead><tr><th>When</th><th>Form</th><th>Organization</th><th>User</th><th>What differed</th><th></th></tr></thead>
          <tbody>
            {attention?.formChanges.map((item) => (
              <tr key={item.id}>
                <td>{new Date(item.createdAt).toLocaleString()}</td>
                <td><Link href={`/admin/forms/${item.formId}`}>{item.address}</Link></td>
                <td>{item.organizationName}</td>
                <td>{item.userEmail}</td>
                <td>{item.text}</td>
                <td><button type="button" onClick={() => startRun(item.id)}>Start a new run</button></td>
              </tr>
            ))}
            {attention && attention.formChanges.length === 0 && <tr><td colSpan={6}>None. The extension adds a row here when the open page no longer matches the model.</td></tr>}
          </tbody>
        </table>
      </section>
      <section>
        <h2>Settings reports</h2>
        <table>
          <thead><tr><th>When</th><th>Organization</th><th>User</th><th>Text</th></tr></thead>
          <tbody>
            {attention?.settings.map((item) => (
              <tr key={item.id}>
                <td>{new Date(item.createdAt).toLocaleString()}</td>
                <td><Link href={`/admin/mismatches?organization=${item.organizationId}`}>{item.organizationName}</Link></td>
                <td>{item.userEmail}</td>
                <td>{item.text}</td>
              </tr>
            ))}
            {attention && attention.settings.length === 0 && <tr><td colSpan={4}>None.</td></tr>}
          </tbody>
        </table>
      </section>
    </main>
  );
}
