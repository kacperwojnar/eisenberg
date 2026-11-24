"use client";

import { useEffect, useState } from "react";
import { api } from "../../../lib/api";

type FileRow = {
  id: string;
  organizationName: string;
  originalFileName: string;
  status: string;
  statusText: string;
  contentUnderstandingEnabled: boolean | null;
};

export default function ContentUnderstandingPage() {
  const [enabled, setEnabled] = useState(false);
  const [connected, setConnected] = useState(false);
  const [files, setFiles] = useState<FileRow[]>([]);
  const [error, setError] = useState("");

  async function load() {
    const global = await api<{ enabled: boolean; connected: boolean }>("/content-understanding");
    setEnabled(global.enabled);
    setConnected(global.connected);
    setFiles(await api<FileRow[]>("/admin/files"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  async function setGlobal(value: boolean) {
    await api("/content-understanding", { method: "PUT", body: JSON.stringify({ enabled: value }) });
    setEnabled(value);
  }

  async function setImport(id: string, value: boolean | null) {
    await api(`/files/${id}/content-understanding`, {
      method: "PUT",
      body: JSON.stringify({ enabled: value })
    });
    await load();
  }

  return (
    <main className="wide">
      <h1>Reading</h1>
      <p className="note">
        Content Understanding is {connected ? "connected" : "not connected"}.
        The most specific switch wins: this import, otherwise the user, otherwise the switch for everyone.
        Files are not uploaded or deleted here.
      </p>
      {error && <p className="failed">{error}</p>}
      <label>
        <input type="checkbox" checked={enabled} onChange={(e) => setGlobal(e.target.checked)} />
        On for everyone
      </label>
      <table>
        <thead><tr><th>Organization</th><th>Import</th><th>Reading</th><th>Switch</th></tr></thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id}>
              <td>{file.organizationName}</td>
              <td>{file.originalFileName}</td>
              <td>{file.statusText || (file.status === "failed" ? "Reading failed." : file.status === "ready" ? "Read and indexed." : file.status === "processing" ? "Reading the document." : "Saved. No reading result yet.")}</td>
              <td>
                <select
                  value={file.contentUnderstandingEnabled === null ? "" : String(file.contentUnderstandingEnabled)}
                  onChange={(e) => setImport(file.id, e.target.value === "" ? null : e.target.value === "true")}
                >
                  <option value="">unset</option>
                  <option value="true">on</option>
                  <option value="false">off</option>
                </select>
              </td>
            </tr>
          ))}
          {files.length === 0 && <tr><td colSpan={4}>No imports yet.</td></tr>}
        </tbody>
      </table>
    </main>
  );
}
