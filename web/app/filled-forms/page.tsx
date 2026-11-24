"use client";

import { useEffect, useState } from "react";
import { api } from "../../lib/api";

type Row = { id: string; createdAt: string; summary: string; formAddress: string; recordLabel: string };

export default function FilledFormsPage() {
  const [rows, setRows] = useState<Row[]>([]);
  const [error, setError] = useState("");

  useEffect(() => {
    api<Row[]>("/filled-forms").then(setRows).catch((e: Error) => setError(e.message));
  }, []);

  return (
    <main className="wide">
      <h1>Filled forms</h1>
      <p className="note">Information only. These records cannot be changed or deleted here. The values that were entered stay on the website that was filled.</p>
      {error && <p className="failed">{error}</p>}
      <table>
        <thead><tr><th>When</th><th>Form</th><th>Record</th><th>Summary</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{new Date(row.createdAt).toLocaleString()}</td>
              <td>{row.formAddress || "—"}</td>
              <td>{row.recordLabel || "—"}</td>
              <td>{row.summary}</td>
            </tr>
          ))}
          {rows.length === 0 && <tr><td colSpan={4}>Nothing has been filled yet.</td></tr>}
        </tbody>
      </table>
    </main>
  );
}
