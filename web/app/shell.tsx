"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { usePathname } from "next/navigation";
import { devRole, setDevRole, type DevRole } from "../lib/api";

const adminLinks = [
  ["/admin", "Overview"],
  ["/admin/organizations", "Organizations"],
  ["/admin/users", "Users"],
  ["/admin/forms", "Forms"],
  ["/admin/mismatches", "Reports"],
  ["/admin/content-understanding", "Reading"]
];

const userLinks = [
  ["/files", "Documents"],
  ["/instances", "Records"],
  ["/filled-forms", "Filled forms"],
  ["/settings", "Settings"]
];

export function Shell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
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
    window.location.href = next === "admin" ? "/admin" : "/files";
  }

  const links = role === "admin" ? adminLinks : userLinks;

  return (
    <div className="app">
      <aside>
        <p className="brand">eisenberg</p>
        <p className="role">{role === "admin" ? "Admin" : "User"}</p>
        <nav>
          {links.map(([href, label]) => (
            <Link key={href} href={href} aria-current={pathname === href ? "page" : undefined}>{label}</Link>
          ))}
        </nav>
        <div className="aside-foot">
          {entra ? <a href="/login">Sign in</a> : (
            <button type="button" onClick={() => choose(role === "admin" ? "user" : "admin")}>
              Switch to {role === "admin" ? "user" : "admin"}
            </button>
          )}
        </div>
      </aside>
      <div className="content">{children}</div>
    </div>
  );
}
