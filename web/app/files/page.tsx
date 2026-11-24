"use client";

import { FormEvent, Fragment, useEffect, useState } from "react";
import { api } from "../../lib/api";

type Organization = { id: string; name: string };
type Me = { organizationIds: string[] };
type FileRow = { id: string; organizationId: string; originalFileName: string; status: string; statusText: string };
type Segment = { page: number | null; text: string; location: string };
type Cited = { id: string; label: string; properties: string[] };
type Reading = { status: string; statusText: string; text: string; segments: Segment[]; records: Cited[] };

function readingSentence(status: string, statusText: string) {
  if (statusText) return statusText;
  if (status === "processing") return "Reading the document.";
  if (status === "ready") return "Read and indexed.";
  if (status === "failed") return "Reading failed. No further detail was stored.";
  return "Saved. The reading appears here after the extractor runs.";
}

export default function FilesPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [files, setFiles] = useState<FileRow[]>([]);
  const [selected, setSelected] = useState<string>("");
  const [reading, setReading] = useState<Reading | null>(null);
  const [error, setError] = useState("");

  async function load(selectedOrganization?: string) {
    const me = await api<Me>("/me");
    const all = await api<Organization[]>("/organizations");
    const mine = all.filter((organization) => me.organizationIds.includes(organization.id));
    setOrganizations(mine);
    const current = selectedOrganization || organizationId || mine[0]?.id || "";
    if (selectedOrganization && selectedOrganization !== organizationId) {
      setSelected("");
      setReading(null);
    }
    setOrganizationId(current);
    setFiles(current ? await api<FileRow[]>(`/files?organizationId=${current}`) : []);
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  useEffect(() => {
    if (!files.some((file) => file.status === "processing")) return;
    const timer = setTimeout(() => load(organizationId).catch((e: Error) => setError(e.message)), 3000);
    return () => clearTimeout(timer);
  }, [files, organizationId]);

  useEffect(() => {
    if (!selected) return;
    let cancelled = false;
    api<Reading>(`/files/${selected}/reading`)
      .then((body) => { if (!cancelled) setReading(body); })
      .catch((e: Error) => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [selected, files]);

  function open(id: string) {
    setReading(null);
    setSelected((current) => current === id ? "" : id);
  }

  async function upload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    form.set("organizationId", organizationId);
    await api("/files", { method: "POST", body: form });
    event.currentTarget.reset();
    await load(organizationId);
  }

  async function remove(id: string) {
    await api(`/files/${id}`, { method: "DELETE" });
    if (selected === id) {
      setSelected("");
      setReading(null);
    }
    await load(organizationId);
  }

  return (
    <main className="wide">
      <h1>Documents</h1>
      <p className="note">Import the files for one organization. The extractor reads each file, keeps the text with its page and place, and builds records for every published form in that organization.</p>
      {error && <p className="failed">{error}</p>}
      <form onSubmit={upload}>
        <select value={organizationId} onChange={(e) => load(e.target.value)}>
          {organizations.map((organization) => (
            <option key={organization.id} value={organization.id}>{organization.name}</option>
          ))}
        </select>
        <input name="file" type="file" required />
        <button type="submit">Import</button>
      </form>
      <table>
        <thead><tr><th>Document</th><th>Extractor</th><th></th><th></th></tr></thead>
        <tbody>
          {files.map((file) => (
            <Fragment key={file.id}>
              <tr>
                <td>{file.originalFileName}</td>
                <td>{readingSentence(file.status, file.statusText)}</td>
                <td><button type="button" onClick={() => open(file.id)}>{selected === file.id ? "Hide reading" : "Reading"}</button></td>
                <td><button type="button" onClick={() => remove(file.id)}>Delete</button></td>
              </tr>
              {selected === file.id && reading && (
                <tr>
                  <td colSpan={4}>
                    <p>{readingSentence(reading.status, reading.statusText)}</p>
                    {reading.records.length > 0
                      ? <p>Records from this document: {reading.records.map((record) => `${record.label || "record"} (${record.properties.join(", ")})`).join("; ")}</p>
                      : <p className="note">No record cites this document yet.</p>}
                    {reading.segments.length > 0 ? reading.segments.map((segment, index) => (
                      <article key={index}>
                        <h3>Page {segment.page ?? index + 1}</h3>
                        <p className="note">{segment.location ? segment.location.slice(0, 160) : "No place on the page was stored."}</p>
                        <p>{segment.text || "No text on this page."}</p>
                      </article>
                    )) : <p>{reading.text || "No reading text yet."}</p>}
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
          {files.length === 0 && <tr><td colSpan={4}>No documents yet.</td></tr>}
        </tbody>
      </table>
    </main>
  );
}
