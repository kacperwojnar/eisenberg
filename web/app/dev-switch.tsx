"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { devRole, setDevRole, type DevRole } from "../lib/api";

export function DevSwitch() {
  const [role, setRole] = useState<DevRole>("user");
  const [entra, setEntra] = useState(false);

  useEffect(() => {
    setRole(devRole());
    fetch("/backend/auth")
      .then((response) => response.json())
      .then((body) => setEntra(body.mode === "entra"))
      .catch(() => undefined);
  }, []);

  function choose(next: DevRole) {
    setDevRole(next);
    setRole(next);
    window.location.href = next === "admin" ? "/admin/organizations" : "/files";
  }

  return (
    <nav>
      <strong>eisenberg</strong>
      {role === "admin" ? (
        <>
          <Link href="/admin/organizations">Organizations</Link>
          <Link href="/admin/users">Users</Link>
          <Link href="/admin/forms">Forms</Link>
          <Link href="/admin/mismatches">Mismatches</Link>
          <Link href="/admin/content-understanding">Content Understanding</Link>
        </>
      ) : (
        <>
          <Link href="/files">Files</Link>
          <Link href="/instances">Records</Link>
          <Link href="/filled-forms">Filled forms</Link>
          <Link href="/settings">Settings</Link>
        </>
      )}
      {entra ? <a href="/login">Sign in</a> : (
        <>
          <span className="note">Development switch. Removed when Entra is connected.</span>
          <button type="button" onClick={() => choose(role === "admin" ? "user" : "admin")}>
            Switch to {role === "admin" ? "user" : "admin"}
          </button>
        </>
      )}
    </nav>
  );
}
