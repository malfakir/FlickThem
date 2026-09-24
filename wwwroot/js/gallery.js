const $ = (sel) => document.querySelector(sel);

const shareId = window.location.pathname.split("/")[2];

// Fetch the share's file list and render thumbnails. Each thumbnail opens the
// (presigned) full-size image in a new tab; the presigned URL expires after an
// hour, at which point the page can simply be reloaded for fresh URLs.
async function init() {
  const res = await fetch(`/api/share/${shareId}/files`);
  if (!res.ok) {
    $("#gallery-error").textContent = "This share does not exist or has expired.";
    $("#gallery-error").classList.remove("hidden");
    return;
  }

  const data = await res.json();
  const files = data.files || [];

  $("#upload-link").href = `/s/${shareId}`;
  $("#gallery-title").textContent = files.length === 0 ? "Photos" : `Photos (${files.length})`;

  if (files.length === 0) {
    $("#gallery-empty").classList.remove("hidden");
    return;
  }

  const grid = $("#grid");
  for (const f of files) {
    const a = document.createElement("a");
    a.href = f.getUrl;
    a.target = "_blank";
    a.rel = "noopener";
    a.innerHTML = `
      <img src="${f.getUrl}" alt="" loading="lazy">
      <span class="cap">
        <span class="nm"></span>
        <span class="sz"></span>
      </span>`;
    a.querySelector(".nm").textContent = f.name;
    a.querySelector(".sz").textContent = formatBytes(f.size);
    grid.appendChild(a);
  }
}

function formatBytes(n) {
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " KB";
  return (n / (1024 * 1024)).toFixed(1) + " MB";
}

init();