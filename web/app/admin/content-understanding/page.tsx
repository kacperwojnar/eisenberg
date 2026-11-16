"use client";

import { useEffect, useState } from "react";
import { api } from "../../../lib/api";

type FileRow = {
  id: string;
  organizationId: string;
  originalFileName: string;
  contentUnderstandingEnabled: boolean | null;
};

export default function ContentUnderstandingPage() {
  const [enabled, setEnabled] = useState(false);
  const [files, setFiles] = useState<FileRow[]>([]);
  const [error, setError] = useState("");

  async function load() {
    const global = await api<{ enabled: boolean }>("/content-understanding");
    setEnabled(global.enabled);
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
    <main>
      <h1>Content Understanding</h1>
      <p className="note">Per-user switch is on the Users page. Which switch wins when they disagree is not decided. The worker currently follows the global switch only.</p>
      {error && <p>{error}</p>}
      <label>
        <input type="checkbox" checked={enabled} onChange={(e) => setGlobal(e.target.checked)} />
        On for everyone
      </label>
      <table>
        <thead><tr><th>Import</th><th>Switch</th></tr></thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id}>
              <td>{file.originalFileName}</td>
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
        </tbody>
      </table>
    </main>
  );
}
