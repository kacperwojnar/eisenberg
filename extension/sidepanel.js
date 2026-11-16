const log = document.getElementById("log");

async function apiBase() {
  const stored = await chrome.storage.sync.get({ apiBase: "http://localhost:5000" });
  return stored.apiBase;
}

function add(text) {
  const p = document.createElement("p");
  p.textContent = text;
  log.appendChild(p);
}

document.getElementById("chat").addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = document.getElementById("message").value;
  add(message);
  const response = await fetch(await apiBase() + "/chat", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Dev-User": "user"
    },
    body: JSON.stringify({ message })
  });
  const body = await response.json();
  add(body.reply);
  document.getElementById("message").value = "";
});

document.getElementById("mismatch").addEventListener("submit", async (event) => {
  event.preventDefault();
  const me = await fetch(await apiBase() + "/me", { headers: { "X-Dev-User": "user" } });
  const user = await me.json();
  await fetch(await apiBase() + "/settings-mismatches", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Dev-User": "user"
    },
    body: JSON.stringify({
      organizationId: user.organizationIds[0],
      text: document.getElementById("report").value
    })
  });
  add("Reported to admin.");
  document.getElementById("report").value = "";
});
