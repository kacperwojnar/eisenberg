if (document.documentElement.dataset.eisenberg === "web") {
  const api = document.querySelector('meta[name="eisenberg-api"]')?.content || "";
  const stored = {
    token: sessionStorage.getItem("eisenberg-token") || "",
    devUser: localStorage.getItem("eisenberg-dev-user") === "admin" ? "admin" : "user"
  };
  chrome.storage.local.set(stored);
  if (api) chrome.storage.sync.set({ apiBase: api });
}
