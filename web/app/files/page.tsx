"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../lib/api";

type Organization = { id: string; name: string };
type Me = { organizationIds: string[] };
type FileRow = { id: string; organizationId: string; originalFileName: string; status: string };

export default function FilesPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [organizationId, setOrganizationId] = useState("");
  const [files, setFiles] = useState<FileRow[]>([]);
  const [error, setError] = useState("");

  async function load(selected?: string) {
    const me = await api<Me>("/me");
    const all = await api<Organization[]>("/organizations");
    const mine = all.filter((organization) => me.organizationIds.includes(organization.id));
    setOrganizations(mine);
    const current = selected || organizationId || mine[0]?.id || "";
    setOrganizationId(current);
    setFiles(current ? await api<FileRow[]>(`/files?organizationId=${current}`) : []);
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

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
    await load(organizationId);
  }

  return (
    <main>
      <h1>Files</h1>
      {error && <p>{error}</p>}
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
        <thead><tr><th>Name</th><th>Status</th><th></th></tr></thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id}>
              <td>{file.originalFileName}</td>
              <td>{file.status}</td>
              <td><button type="button" onClick={() => remove(file.id)}>Delete</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </main>
  );
}
