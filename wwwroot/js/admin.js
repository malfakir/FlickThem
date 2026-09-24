const $ = (sel) => document.querySelector(sel);

let shares = [];

async function api(url, options) {
  const res = await fetch(url, options);
  if (res.status === 401) throw new Error("unauthorized");
  return res;
}

async function loadShares() {
  let res;
  try {
    res = await api("/api/admin/shares");
  } catch (err) {
    if (err.message === "unauthorized") {
      $("#login").classList.remove("hidden");
      return;
    }
    throw err;
  }
  shares = await res.json();
  render();
  loadUsage();
}

// Shows how much of the R2 bucket capacity is used. The bar stays hidden when
// usage can't be measured (no R2:ApiToken configured) — a small hint explains
// how to enable it.
async function loadUsage() {
  const status = $("#storage-status");
  const barWrap = $("#storage-bar-wrap");
  const bar = $("#storage-bar");
  barWrap.classList.add("hidden");

  let res;
  try {
    res = await api("/api/admin/usage");
  } catch {
    status.textContent = "Storage usage unavailable.";
    return;
  }

  const data = await res.json();
  if (data.usageBytes == null) {
    status.textContent = "Storage usage unavailable \u2014 set R2:ApiToken to show it.";
    return;
  }

  const used = data.usageBytes;
  const limit = data.limitBytes;
  const headerBytes = data.headroomBytes;
  const pct = Math.min(100, (used / limit) * 100);

  status.textContent =
    "Storage: " + formatBytes(used) + " of " + formatBytes(limit) +
    " used (" + pct.toFixed(1) + "%). Uploads stop automatically at " +
    formatBytes(limit - headerBytes) + ".";

  bar.style.width = pct.toFixed(1) + "%";
  bar.classList.toggle("warn", pct >= 80);
  bar.classList.toggle("full", pct >= 98 || used >= limit - headerBytes);
  barWrap.classList.remove("hidden");
}

function formatBytes(n) {
  if (!Number.isFinite(n) || n < 0) return "?";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return n.toFixed(i === 0 ? 0 : 1) + " " + units[i];
}

function render() {
  $("#admin").classList.remove("hidden");
  $("#logout").classList.remove("hidden");

  const list = $("#share-list");
  list.innerHTML = "";

  $("#share-empty").classList.toggle("hidden", shares.length > 0);

  for (const s of shares) {
    const li = document.createElement("li");
    li.className = "share-item";

    const grow = document.createElement("div");
    grow.className = "grow";
    grow.innerHTML = `<div class="name"></div><div class="meta"></div>`;
    grow.querySelector(".name").textContent = s.name;
    grow.querySelector(".meta").textContent =
      new Date(s.createdAtUtc).toLocaleString() +
      (s.expiresAtUtc ? " \u00b7 expires " + new Date(s.expiresAtUtc).toLocaleString() : "");

    const actions = document.createElement("div");
    actions.className = "share-actions";

    const qrBtn = button("QR", () => showQr(s), "btn ghost");
    const copyBtn = button("Copy link", () => copyText(s.uploadUrl), "btn ghost");
    const delBtn = button("Delete", () => deleteShare(s), "btn danger");

    actions.append(qrBtn, copyBtn, delBtn);
    li.append(grow, actions);
    list.appendChild(li);
  }
}

function button(label, onClick, className) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = className;
  b.textContent = label;
  b.addEventListener("click", onClick);
  return b;
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = document.createElement("textarea");
    ta.value = text;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
  }
}

async function showQr(share) {
  const res = await api(`/api/share/${share.id}/qr.svg`);
  const svg = await res.text();

  const modal = $("#modal");
  $("#modal-content").innerHTML = `
    <h2 class="qr-head">${escapeHtml(share.name)}</h2>
    <div class="qr">${svg}</div>
    <p class="muted">Scan to open the upload page.</p>
    <div class="codebox"><span>${escapeHtml(share.uploadUrl)}</span></div>
    <div style="display:flex; gap:8px; justify-content:center;">
      <a class="btn primary" href="/api/share/${share.id}/qr.svg" download="flickthem-qr.svg">Download SVG</a>
      <button class="btn" type="button" onclick="copyText('${share.uploadUrl.replace(/'/g, "\\'")}')">Copy link</button>
    </div>`;
  modal.classList.remove("hidden");
}

function closeModal() {
  $("#modal").classList.add("hidden");
}

async function deleteShare(share) {
  if (!confirm(`Delete share "${share.name}"? Uploaded photos will also be removed.`)) return;
  await api(`/api/admin/shares/${share.id}/delete`, { method: "POST" });
  await loadShares();
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

$("#login-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const pin = $("#pin").value;
  const res = await fetch("/api/admin/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pin })
  });
  if (!res.ok) {
    $("#login-error").textContent = "Wrong PIN.";
    $("#login-error").classList.remove("hidden");
    return;
  }
  $("#pin").value = "";
  $("#login-error").classList.add("hidden");
  $("#login").classList.add("hidden");
  await loadShares();
});

$("#create-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const name = $("#share-name").value.trim();
  await api("/api/admin/shares", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ name })
  });
  $("#share-name").value = "";
  await loadShares();
});

$("#logout").addEventListener("click", async () => {
  await fetch("/api/admin/logout", { method: "POST" });
  $("#admin").classList.add("hidden");
  $("#logout").classList.add("hidden");
  $("#login").classList.remove("hidden");
});

$("#modal").addEventListener("click", (e) => {
  if (e.target === $("#modal")) closeModal();
});

loadShares();