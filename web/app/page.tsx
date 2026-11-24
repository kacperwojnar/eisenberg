"use client";

import { useEffect } from "react";
import { devRole } from "../lib/api";

export default function Home() {
  useEffect(() => {
    window.location.replace(devRole() === "admin" ? "/admin" : "/files");
  }, []);
  return <main>eisenberg</main>;
}
