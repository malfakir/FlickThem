# FlickThem — Setup & Deployment Guide

Step-by-step guide to register for Cloudflare R2, configure the app, and publish it so QR codes work publicly.

## Part 1 — Register for Cloudflare R2 (free-ish)

1. Go to **dash.cloudflare.com** → **Sign up** (email + password).
2. In the left sidebar click **R2 Object Storage** → it'll prompt to add a billing method (a card, but you won't be charged while under the free tier: 10GB, 10M reads/mo).
3. Click **Create bucket** → name it e.g. `flickthem-photos`, region **APAC auto** (free) → **Create bucket**.
4. Copy your **Account ID** (right-hand column of the R2 dashboard) — you'll need it below.
5. **Manage R2 API Tokens** → **Create API Token** → choose **Object Read & Write** → select your bucket → **Create**.
   - Copy **Access Key ID** and **Secret Access Key** now (shown only once).
6. **Bucket → Settings → CORS Policy → Add**:
   ```json
   [
     {
       "AllowedOrigins": ["https://your-domain.com", "http://localhost:5032"],
       "AllowedMethods": ["GET", "PUT"],
       "AllowedHeaders": ["*"],
       "ExposeHeaders": ["ETag"],
       "MaxAgeSeconds": 3600
     }
   ]
   ```
   Replace `your-domain.com` with your public URL. This is required for browser→R2 uploads.

## Part 2 — Configure the app

In `appsettings.json` (or as env vars):

```jsonc
"R2": {
  "AccountId": "0000...your-account-id",
  "AccessKeyId": "your-access-key",
  "SecretAccessKey": "your-secret",
  "Bucket": "flickthem-photos"
},
"Admin": { "Pin": "a-strong-pin" },
"Site": { "BaseUrl": "https://your-domain.com" }
```

Test locally with `dotnet run` — uploads, gallery, and QR should now work for real.

## Part 3 — Publish/deploy (pick one)

### Option A — Your own PC/NAS via Cloudflare Tunnel (free, no port forwarding)

1. Download `cloudflared` from Cloudflare, then run `cloudflared tunnel login` → pick your Cloudflare account/domain.
2. `cloudflared tunnel create flickthem`
3. `cloudflared tunnel route dns flickthem your-domain.com`
4. Run the app on `:5032` and expose it: `cloudflared tunnel run flickthem`
5. Give Cloudflare a moment to issue the certificate (HTTPS auto).

### Option B — Docker anywhere (VPS/Home server)

```bash
docker build -t flickthem .
docker run -d --name flickthem -p 8080:8080 \
  -e R2__AccountId=... -e R2__AccessKeyId=... -e R2__SecretAccessKey=... \
  -e R2__Bucket=flickthem-photos -e Admin__Pin=... \
  -e Site__BaseUrl=https://your-domain.com \
  -v flickthem-data:/app/data flickthem
```

(Dockerfile exposes `:8080` internally.)

### Option C — Cloud hosts (Azure App Service, Railway, Fly.io, Render)

Deploy the Dockerfile via their UI; set the same env vars; they give you an HTTPS `.app` / `.railway` URL — use that as `Site:BaseUrl`.

## Part 4 — Verify

1. Open `https://your-domain.com` → sign in with `Admin:Pin`.
2. Create a share → **Download SVG** the QR → print/screen it.
3. Scan with your phone → drag & drop a few photos.
4. Open the same share's gallery → photos are there, downloadable.
5. Exit the browser; scan the QR again from a different device — uploads still go straight into your R2 bucket (visible in the Cloudflare dashboard).

## Gotchas

- **HTTPS matters**: QR scanning/phones assume `https://` — Cloudflare Tunnel and all cloud hosts give you this for free.
- The **QR encodes the public upload URL**, so do the QR test only after `Site:BaseUrl` points to the real, reachable site.
- Free R2 is **10GB total** — watch it in the dashboard; auto-cleanup of old shares can be added later.