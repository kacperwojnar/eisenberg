"use client";

import { FormEvent, useEffect, useState } from "react";
import { api } from "../../../lib/api";

type Organization = { id: string; name: string };
type User = {
  id: string;
  email: string;
  role: string;
  contentUnderstandingEnabled: boolean | null;
  organizationIds: string[];
};

export default function UsersPage() {
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [users, setUsers] = useState<User[]>([]);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState("user");
  const [selected, setSelected] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [addTo, setAddTo] = useState<Record<string, string>>({});

  function nameOf(id: string) {
    return organizations.find((organization) => organization.id === id)?.name || id;
  }

  async function load() {
    setOrganizations(await api<Organization[]>("/organizations"));
    setUsers(await api<User[]>("/users"));
  }

  useEffect(() => {
    load().catch((e: Error) => setError(e.message));
  }, []);

  function toggle(id: string) {
    setSelected((current) => current.includes(id) ? current.filter((x) => x !== id) : [...current, id]);
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    await api("/users", {
      method: "POST",
      body: JSON.stringify({ email, role, organizationIds: selected })
    });
    setEmail("");
    setSelected([]);
    await load();
  }

  async function addOrganization(id: string) {
    const organizationId = addTo[id];
    if (!organizationId) return;
    await api(`/users/${id}/organizations`, { method: "POST", body: JSON.stringify({ organizationId }) });
    await load();
  }

  async function setSwitch(id: string, enabled: boolean | null) {
    await api(`/users/${id}/content-understanding`, {
      method: "PUT",
      body: JSON.stringify({ enabled })
    });
    await load();
  }

  return (
    <main>
      <h1>Users</h1>
      {error && <p>{error}</p>}
      <form onSubmit={create}>
        <input value={email} onChange={(e) => setEmail(e.target.value)} placeholder="Email" required />
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="user">user</option>
          <option value="admin">admin</option>
        </select>
        {organizations.map((organization) => (
          <label key={organization.id}>
            <input type="checkbox" checked={selected.includes(organization.id)} onChange={() => toggle(organization.id)} />
            {organization.name}
          </label>
        ))}
        <button type="submit">Create</button>
      </form>
      <table>
        <thead><tr><th>Email</th><th>Role</th><th>Organizations</th><th>Content Understanding</th></tr></thead>
        <tbody>
          {users.map((user) => (
            <tr key={user.id}>
              <td>{user.email}</td>
              <td>{user.role}</td>
              <td>
                {user.organizationIds.map(nameOf).join(", ") || "none"}
                <form className="row" onSubmit={(event) => { event.preventDefault(); addOrganization(user.id); }}>
                  <select value={addTo[user.id] || ""} onChange={(e) => setAddTo((current) => ({ ...current, [user.id]: e.target.value }))}>
                    <option value="">Add to</option>
                    {organizations.filter((organization) => !user.organizationIds.includes(organization.id)).map((organization) => (
                      <option key={organization.id} value={organization.id}>{organization.name}</option>
                    ))}
                  </select>
                  <button type="submit">Add</button>
                </form>
              </td>
              <td>
                <select
                  value={user.contentUnderstandingEnabled === null ? "" : String(user.contentUnderstandingEnabled)}
                  onChange={(e) => setSwitch(user.id, e.target.value === "" ? null : e.target.value === "true")}
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
