"use client";

import { useEffect, useState } from "react";

function random(bytes: number) {
  const values = crypto.getRandomValues(new Uint8Array(bytes));
  return btoa(String.fromCharCode(...values)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

async function challenge(verifier: string) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return btoa(String.fromCharCode(...new Uint8Array(digest))).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

export default function LoginPage() {
  const [message, setMessage] = useState("Checking sign-in.");

  useEffect(() => {
    const run = async () => {
      const config = await fetch("/backend/auth").then((response) => response.json());
      if (config.mode !== "entra") {
        window.location.replace("/");
        return;
      }
      const query = new URLSearchParams(window.location.search);
      const code = query.get("code");
      const verifier = sessionStorage.getItem("eisenberg-verifier");
      if (code && verifier) {
        const body = new URLSearchParams({
          client_id: config.clientId,
          grant_type: "authorization_code",
          code,
          redirect_uri: window.location.origin + "/login",
          code_verifier: verifier
        });
        const token = await fetch(`https://${config.tenantId}.ciamlogin.com/${config.tenantId}/oauth2/v2.0/token`, {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded" },
          body
        });
        const payload = await token.json();
        if (!token.ok || !payload.id_token) {
          setMessage("Sign-in did not return a token.");
          return;
        }
        sessionStorage.setItem("eisenberg-token", payload.id_token);
        sessionStorage.removeItem("eisenberg-verifier");
        window.location.replace("/");
        return;
      }
      const nextVerifier = random(32);
      sessionStorage.setItem("eisenberg-verifier", nextVerifier);
      const url = new URL(`https://${config.tenantId}.ciamlogin.com/${config.tenantId}/oauth2/v2.0/authorize`);
      url.searchParams.set("client_id", config.clientId);
      url.searchParams.set("response_type", "code");
      url.searchParams.set("redirect_uri", window.location.origin + "/login");
      url.searchParams.set("scope", "openid email profile");
      url.searchParams.set("code_challenge", await challenge(nextVerifier));
      url.searchParams.set("code_challenge_method", "S256");
      window.location.replace(url.toString());
    };
    run().catch(() => setMessage("Sign-in failed."));
  }, []);

  return <main><p>{message}</p></main>;
}
