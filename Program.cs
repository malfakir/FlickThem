using System.Security.Cryptography;
using System.Text;
using FlickThem.Models;
using FlickThem.Services;

// FlickThem — a QR-code photo drop website.
//
// Flow:
//   1. The owner (admin) creates a "share" and prints/scans its QR code.
//   2. The QR encodes /s/{shareId} — the public upload page.
//   3. Visitors drag & drop photos; the browser PUTs them DIRECTLY to
//      Cloudflare R2 using presigned URLs (no photos pass through this server).
//   4. The owner views/downloads photos on the gallery page /g/{shareId}.

var builder = WebApplication.CreateBuilder(args);

// R2Storage talks to Cloudflare R2 via the S3-compatible API.
builder.Services.Configure<R2Options>(builder.Configuration.GetSection("R2"));
builder.Services.AddSingleton<R2Storage>();

// ShareStore keeps the share index in memory, mirrored to a local JSON file
// and, when R2 is configured, also uploaded to R2 (meta/shares.json) so a
// fresh instance can reload it after restarts/redeploys.
builder.Services.AddSingleton(sp =>
    ShareStore.CreateAsync(
            builder.Configuration["Storage:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data"),
            sp.GetRequiredService<R2Storage>(),
            CancellationToken.None)
        .GetAwaiter().GetResult());
builder.Services.AddSingleton<QrCodeService>();

var app = builder.Build();

// Ensure the share index is loaded (from disk or R2) before serving requests.
_ = app.Services.GetRequiredService<ShareStore>();

// ---- Admin auth ------------------------------------------------------------
// Very simple cookie auth: the admin enters a PIN, we set a cookie whose
// value is SHA256("ft:" + pin). There is no user system or database.
var adminPin = builder.Configuration["Admin:Pin"] ?? "";
const string adminCookie = "ft_admin";

static string AdminToken(string pin) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ft:" + pin)));

bool IsAdmin(HttpContext ctx) =>
    !string.IsNullOrEmpty(adminPin) && ctx.Request.Cookies[adminCookie] == AdminToken(adminPin);

// QR codes and share links must use the PUBLIC base URL (Site:BaseUrl), which
// can differ from the host the request actually arrived on (e.g. reverse
// proxies, tunnels, or a new custom domain).
string BaseUrl(HttpContext ctx)
{
    var configured = builder.Configuration["Site:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
    return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
}

string ShareUploadUrl(HttpContext ctx, string id) => $"{BaseUrl(ctx)}/s/{id}";
string ShareGalleryUrl(HttpContext ctx, string id) => $"{BaseUrl(ctx)}/g/{id}";

// Upload safety limits shared by the API endpoints.
const int MaxUploadsPerRequest = 100;
const long MaxFileSizeBytes = 20L * 1024 * 1024;

// Storage capacity: stop accepting uploads before the R2 bucket cap is hit.
// Configurable so it can be lowered for testing (e.g. 100 MB) without waiting
// to fill 10 GB. "Headroom" keeps a safety margin below the hard cap so the
// app still refuses uploads before Cloudflare ever blocks them.
long maxStorageBytes = builder.Configuration.GetValue<long?>("Limits:MaxStorageBytes") ?? 10L * 1024 * 1024 * 1024;
long headroomBytes = builder.Configuration.GetValue<long?>("Limits:HeadroomBytes") ?? 100L * 1024 * 1024;

// ---- Admin session ----

app.MapPost("/api/admin/login", (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromBody] LoginRequest req) =>
{
    if (string.IsNullOrEmpty(adminPin) || req.Pin != adminPin)
        return Results.Unauthorized();

    ctx.Response.Cookies.Append(adminCookie, AdminToken(adminPin), new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = !app.Environment.IsDevelopment(),
        Expires = DateTimeOffset.UtcNow.AddDays(30)
    });
    return Results.Ok();
});

app.MapPost("/api/admin/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(adminCookie);
    return Results.Ok();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// ---- Admin share management ----

app.MapGet("/api/admin/shares", (HttpContext ctx, ShareStore store, R2Storage r2) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var shares = store.All()
        .OrderByDescending(s => s.CreatedAtUtc)
        .Select(s => new
        {
            s.Id,
            s.Name,
            s.CreatedAtUtc,
            s.ExpiresAtUtc,
            uploadUrl = ShareUploadUrl(ctx, s.Id),
            galleryUrl = ShareGalleryUrl(ctx, s.Id)
        });
    return Results.Ok(shares);
});

app.MapPost("/api/admin/shares", async (HttpContext ctx, ShareStore store, [Microsoft.AspNetCore.Mvc.FromBody] CreateShareRequest req, CancellationToken ct) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var share = await store.CreateAsync(req.Name ?? "", ct);
    return Results.Ok(new
    {
        share.Id,
        share.Name,
        share.CreatedAtUtc,
        uploadUrl = ShareUploadUrl(ctx, share.Id),
        galleryUrl = ShareGalleryUrl(ctx, share.Id)
    });
});

app.MapPost("/api/admin/shares/{id}/delete", async (HttpContext ctx, string id, ShareStore store, R2Storage r2, CancellationToken ct) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    if (!await store.DeleteAsync(id, ct)) return Results.NotFound();

    try
    {
        await r2.DeleteShareAsync(id, ct); // remove uploaded photos for that share
    }
    catch
    {
        // Index entry is already gone; orphaned objects would be swept later if any.
    }
    return Results.Ok();
});

// Storage usage for the admin console. usageBytes is null when no Cloudflare
// API token is configured (R2:ApiToken) so the check cannot run.
app.MapGet("/api/admin/usage", async (HttpContext ctx, R2Storage r2, CancellationToken ct) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var usage = await r2.GetUsageBytesAsync(ct);
    return Results.Ok(new { usageBytes = usage, limitBytes = maxStorageBytes, headroomBytes });
});

// ---- Public share pages / info ----
// These endpoints are PUBLIC: anyone with the share ID (i.e. the QR code)
// may read share info, request upload slots, and list files.

app.MapGet("/api/share/{id}", (HttpContext ctx, ShareStore store, string id) =>
{
    var share = store.Get(id);
    if (share is null) return Results.NotFound();
    if (share.ExpiresAtUtc is not null && share.ExpiresAtUtc < DateTime.UtcNow) return Results.NotFound();

    return Results.Ok(new
    {
        share.Id,
        share.Name,
        uploadUrl = ShareUploadUrl(ctx, share.Id),
        galleryUrl = ShareGalleryUrl(ctx, share.Id),
        share.ExpiresAtUtc
    });
});

app.MapPost("/api/share/{id}/slots", async (HttpContext ctx, ShareStore store, R2Storage r2, string id, [Microsoft.AspNetCore.Mvc.FromBody] UploadSlotsRequest req, CancellationToken ct) =>
{
    var share = store.Get(id);
    if (share is null) return Results.NotFound();
    if (share.ExpiresAtUtc is not null && share.ExpiresAtUtc < DateTime.UtcNow) return Results.NotFound();

    // Validate everything BEFORE minting presigned PUT URLs.
    var files = req.Files ?? new List<UploadFileRequest>();
    if (files.Count == 0 || files.Count > MaxUploadsPerRequest)
        return Results.BadRequest(new { error = $"Upload between 1 and {MaxUploadsPerRequest} files." });

    foreach (var f in files)
    {
        if (string.IsNullOrWhiteSpace(f.Name))
            return Results.BadRequest(new { error = "Every file needs a name." });
        if (!f.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"'{f.Name}' is not an image file." });
        if (f.Size <= 0 || f.Size > MaxFileSizeBytes)
            return Results.BadRequest(new { error = $"'{f.Name}' exceeds the 20 MB size limit." });
    }

    // Hand back one presigned PUT URL per file. The browser uploads straight
    // to R2, so large photo files never pass through this server.
    var slots = new List<object>();
    foreach (var f in files)
    {
        var key = R2Storage.BuildObjectKey(id, f.Name);
        slots.Add(new
        {
            name = f.Name,
            objectKey = key,
            putUrl = r2.CreatePutUrl(key, TimeSpan.FromHours(2)),
            size = f.Size,
            mime = f.Mime
        });
    }

    // Capacity gate: ask Cloudflare how much is already stored, and refuse the
    // whole batch if this upload would push the bucket past the limit (minus
    // headroom). Skipped entirely when no Cloudflare API token is configured.
    var usage = await r2.GetUsageBytesAsync(ct);
    if (usage.HasValue)
    {
        var incoming = files.Sum(f => f.Size);
        if (usage.Value + incoming > maxStorageBytes - headroomBytes)
            return Results.Json(
                new { error = "Storage is full — no more uploads can be accepted for now." },
                statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new { slots });
});

app.MapGet("/api/share/{id}/files", async (ShareStore store, R2Storage r2, string id, CancellationToken ct) =>
{
    var share = store.Get(id);
    if (share is null) return Results.NotFound();
    if (share.ExpiresAtUtc is not null && share.ExpiresAtUtc < DateTime.UtcNow) return Results.NotFound();

    if (app.Environment.IsDevelopment() && !r2.IsConfigured)
    {
        // Development demo: pretend a couple of photos already exist so the
        // gallery UI is visible without a configured R2 bucket.
        return Results.Ok(new
        {
            files = new[]
            {
                new { key = "demo-1", name = "flickthem-demo.png", size = 1258291L, lastModifiedUtc = DateTime.UtcNow.AddMinutes(-15), getUrl = "/demo/flickthem-demo.svg" },
                new { key = "demo-2", name = "beach-sunset.jpg", size = 2097152L, lastModifiedUtc = DateTime.UtcNow.AddMinutes(-10), getUrl = "/demo/flickthem-demo.svg" }
            }
        });
    }

    var files = await r2.ListFilesAsync(id, ct);
    return Results.Ok(new { files });
});

// ---- QR code (image/svg+xml) ----

app.MapGet("/api/share/{id}/qr.svg", (HttpContext ctx, ShareStore store, QrCodeService qr, string id) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var share = store.Get(id);
    if (share is null) return Results.NotFound();

    return Results.Content(qr.Svg(ShareUploadUrl(ctx, share.Id)), "image/svg+xml");
});

// ---- Static pages ----
// index.html = admin console at "/".
// s.html     = the public drag & drop upload page served at /s/{shareId}.
// g.html     = the gallery (view/download photos) served at /g/{shareId}.

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapFallback(async ctx =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (path == "/") await ctx.Response.SendFileAsync("wwwroot/index.html");
    else if (path.StartsWith("/s/", StringComparison.OrdinalIgnoreCase)) await ctx.Response.SendFileAsync("wwwroot/s.html");
    else if (path.StartsWith("/g/", StringComparison.OrdinalIgnoreCase)) await ctx.Response.SendFileAsync("wwwroot/g.html");
    else
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("Not found");
    }
});

app.Run();

// JSON request bodies for the minimal API endpoints.
public sealed record LoginRequest(string Pin);
public sealed record CreateShareRequest(string? Name);
public sealed record UploadFileRequest(string Name, string Mime, long Size);
public sealed record UploadSlotsRequest(List<UploadFileRequest>? Files);