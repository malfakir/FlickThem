const $ = (sel) => document.querySelector(sel);

const shareId = window.location.pathname.split("/")[2];
const MAX_BATCH = 100; // max files minted per /slots request
const MAX_SIZE = 20 * 1024 * 1024; // must match the server limit

let pending = [];
let uploading = false;

// Load the share name/gallery link, or show an error for unknown/expired shares.
async function init() {
  const res = await fetch(`/api/share/${shareId}`);
  if (!res.ok) {
    $("#share-error").textContent = "This share does not exist or has expired.";
    $("#share-error").classList.remove("hidden");
    $("#drop").classList.add("hidden");
    return;
  }
  const share = await res.json();
  $("#share-title").textContent = share.name;
  $("#gallery-link").href = share.galleryUrl;
}

// Drop/browse only accept images within the size limit; anything else is skipped.
function addFiles(files) {
  const images = Array.from(files).filter(
    (f) => f.type.startsWith("image/") && f.size > 0 && f.size <= MAX_SIZE
  );
  if (images.length !== files.length) {
    alert("Only image files up to 20 MB are accepted. Others were skipped.");
  }
  pending = pending.concat(images);
  renderQueue();
}

function renderQueue() {
  if (pending.length === 0) {
    $("#queue").classList.add("hidden");
    $("#upload-btn").classList.add("hidden");
    return;
  }

  const list = $("#file-list");
  list.innerHTML = "";

  for (const f of pending) {
    const li = document.createElement("li");
    li.className = "file-item";
    li.innerHTML = `
      <div class="file-head">
        <span class="fname">${escapeHtml(f.name)}</span>
        <span class="fstat">${formatBytes(f.size)}</span>
      </div>
      <div class="progress hidden"><div></div></div>`;
    li.dataset.name = f.name;
    list.appendChild(li);
  }

  $("#queue").classList.remove("hidden");
  $("#upload-btn").classList.remove("hidden");
}

// Upload queued files in batches: ask the server for presigned PUT URLs, then
// upload each file straight to R2 (photos never transit this server).
async function uploadAll() {
  if (uploading) return;
  uploading = true;
  $("#upload-btn").disabled = true;

  const batches = [];
  for (let i = 0; i < pending.length; i += MAX_BATCH) {
    batches.push(pending.slice(i, i + MAX_BATCH));
  }

  try {
    for (const batch of batches) {
      const slotsRes = await fetch(`/api/share/${shareId}/slots`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          files: batch.map((f) => ({ name: f.name, mime: f.type || "image/octet-stream", size: f.size }))
        })
      });

      // e.g. server-side validation: 20 MB too big, storage capacity reached (403).
      if (!slotsRes.ok) {
        const err = (await slotsRes.json()).error || "Upload failed.";
        alert(err);
        throw new Error(err);
      }

      const { slots } = await slotsRes.json();
      await Promise.all(slots.map((slot, i) => uploadOne(batch[i], slot)));
    }

    $("#queue").classList.add("hidden");
    $("#done").classList.remove("hidden");
    pending = [];
  } finally {
    uploading = false;
    $("#upload-btn").disabled = false;
  }
}

// PUT one file to the presigned URL, showing per-file progress.
function uploadOne(file, slot) {
  return new Promise((resolve) => {
    const li = findItem(file.name);
    const bar = li.querySelector(".progress");
    const fill = bar.querySelector("div");
    const stat = li.querySelector(".fstat");

    bar.classList.remove("hidden");
    stat.textContent = "0%";

    const xhr = new XMLHttpRequest();
    xhr.open("PUT", slot.putUrl);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) {
        const pct = Math.round((e.loaded / e.total) * 100);
        fill.style.width = pct + "%";
        stat.textContent = pct + "%";
      }
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        fill.style.width = "100%";
        stat.textContent = "Uploaded";
        li.classList.add("ok");
      } else {
        stat.textContent = "Failed";
        li.classList.add("fail");
      }
      resolve();
    };
    xhr.onerror = () => {
      stat.textContent = "Failed";
      li.classList.add("fail");
      resolve();
    };
    xhr.send(file);
  });
}

function findItem(name) {
  return [...$("#file-list").children].find((li) => li.dataset.name === name) || $("#file-list").children[0];
}

function formatBytes(n) {
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " KB";
  return (n / (1024 * 1024)).toFixed(1) + " MB";
}

function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

const drop = $("#drop");

drop.addEventListener("click", () => $("#file").click());
drop.addEventListener("dragover", (e) => {
  e.preventDefault();
  drop.classList.add("dragover");
});
drop.addEventListener("dragleave", () => drop.classList.remove("dragover"));
drop.addEventListener("drop", (e) => {
  e.preventDefault();
  drop.classList.remove("dragover");
  addFiles(e.dataTransfer.files);
});
$("#file").addEventListener("change", (e) => {
  addFiles(e.target.files);
  e.target.value = "";
});
$("#upload-btn").addEventListener("click", uploadAll);
$("#again").addEventListener("click", () => {
  $("#done").classList.add("hidden");
  $("#drop").classList.remove("hidden");
});

init();