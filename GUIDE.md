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
7. **Storage monitoring token** (optional but recommended): the app can show how full the bucket is and stop uploads before the 10GB free cap is hit.
   - Go to **My Profile (top right) → API Tokens → Create Token** (NOT the R2 API token above, which only works for S3).
   - Choose **Create Custom Token** → name it e.g. `flickthem-usage` → **Permissions → Account → R2 Storage → Read**.
   - **Create** and copy the token now. It's stored in `R2:ApiToken` below.

## Part 2 — Configure the app

In `appsettings.json` (or as env vars):

```jsonc
"R2": {
  "AccountId": "0000...your-account-id",
  "AccessKeyId": "your-access-key",
  "SecretAccessKey": "your-secret",
  "Bucket": "flickthem-photos",
  "ApiToken": "your-storage-monitoring-token"
},
"Limits": {
  "MaxStorageBytes": 10737418240,   // 10 GB — the R2 free-tier cap
  "HeadroomBytes": 104857600        // 100 MB safety margin below the cap
},
"Admin": { "Pin": "a-strong-pin" },
"Site": { "BaseUrl": "https://your-domain.com" }
```

- `ApiToken` is optional. Without it the storage gauge stays "unavailable" and no capacity checks run.
- `MaxStorageBytes` is what uploads are measured against; the app refuses new uploads once usage + batch size would exceed `MaxStorageBytes - HeadroomBytes`. Lower it for testing (e.g. `104857600` = 100 MB) to see the cutoff.

Test locally with `dotnet run` — uploads, gallery, and QR should now work for real. The admin console shows a **Storage** bar with usage against the limit.

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

### Option C — Render (free tier, no credit card) — recommended

The Dockerfile already listens on the `PORT` Render assigns.

1. Sign up at **render.com** → **New → Web Service** → **Connect GitHub repo** (make sure the FlickThem repo is public, or connect your GitHub account).
2. Settings:
   - **Runtime**: `Docker` (Render will use the Dockerfile automatically).
   - **Region**: choose one near your guests (e.g. `Frankfurt (EU Central)`).
   - **Plan**: **Free** (goes to sleep after ~15 min of no traffic; a QR scan wakes it in a few seconds).
3. **Environment variables** (all in section *Environment*):
   ```
   R2__AccountId=            <your account id>
   R2__AccessKeyId=          <your access key>
   R2__SecretAccessKey=      <your secret key>
   R2__Bucket=flickthem-photos
   R2__ApiToken=             <the storage-monitoring token from Part 1.7>   (optional)
   Admin__Pin=               <a strong pin>
   Site__BaseUrl=            <leave empty now — set after step 4>
   ```
4. Click **Create Web Service** → wait for the deploy → the service page shows your URL, e.g. `https://flickthem.onrender.com`.
5. **Copy that URL** and:
   - Set `Site__BaseUrl=https://flickthem.onrender.com` in Render (Service → Environment → Save → **Deploy** again).
   - Add it to the **R2 CORS policy** (`AllowedOrigins`) next to `http://localhost:5032` so phones can upload directly to R2.
6. Open your URL, sign in, and create a *new* share + QR — the QR encodes the Render URL.

Notes:
- **Ephemeral disk**: Render's free tier wipes the disk on redeploy, but the share list is auto-mirrored to R2 (`meta/shares.json`) and reloaded, so shares survive redeploys.
- **Sleep/wake**: free services sleep; a visitor hitting the URL wakes it automatically.
- Free Render gives 750 CPU hours/month; one instance is way below that.

## Part 4 — Verify

1. Open `https://your-domain.com` → sign in with `Admin:Pin`.
2. Create a share → **Download SVG** the QR → print/screen it.
3. Scan with your phone → drag & drop a few photos.
4. Open the same share's gallery → photos are there, downloadable.
5. Exit the browser; scan the QR again from a different device — uploads still go straight into your R2 bucket (visible in the Cloudflare dashboard).

## Gotchas

- **HTTPS matters**: QR scanning/phones assume `https://` — Cloudflare Tunnel and all cloud hosts give you this for free.
- The **QR encodes the public upload URL**, so do the QR test only after `Site:BaseUrl` points to the real, reachable site.
- Free R2 is **10GB total** — the app now shows usage in the admin console and automatically refuses new uploads near the cap (see `Limits` in Part 2); the `R2:ApiToken` must be set in Render for this to work.
- Render's free tier **sleeps** after ~15 min idle — a QR scan wakes it (adds ~5 s first load). Auto-wake on request is built in.