const input = document.getElementById("apiBase");
chrome.storage.sync.get({ apiBase: "http://localhost:5000" }, (stored) => {
  input.value = stored.apiBase;
});
document.getElementById("options").addEventListener("submit", (event) => {
  event.preventDefault();
  chrome.storage.sync.set({ apiBase: input.value });
});
