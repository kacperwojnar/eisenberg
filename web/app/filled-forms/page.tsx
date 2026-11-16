"use client";

import { useEffect, useState } from "react";
import { api } from "../../lib/api";

type Row = { id: string; createdAt: string; summary: string };

export default function FilledFormsPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [error, setError] = useState("");

  useEffect(() => {
    api<Row[]>("/filled-forms").then(setRows).catch((e: Error) => setError(e.message));
  }, []);

  return (
    <main>
      <h1>Filled forms</h1>
      <p className="note">Information only. These records cannot be changed or deleted here.</p>
      {error && <p>{error}</p>}
      <table>
        <thead><tr><th>When</th><th>Summary</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{new Date(row.createdAt).toLocaleString()}</td>
              <td>{row.summary}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </main>
  );
}
