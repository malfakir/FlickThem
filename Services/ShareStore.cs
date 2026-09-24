using System.Security.Cryptography;
using System.Text.Json;
using FlickThem.Models;

namespace FlickThem.Services;

/// <summary>
/// In-memory share index, persisted to a local JSON file AND mirrored to R2
/// (meta/shares.json) so a fresh instance (e.g. an ephemeral Render free tier
/// filesystem) can recover the share list after restarts/redeploys.
/// </summary>
public sealed class ShareStore
{
    private const string MetaKey = "meta/shares.json";

    private readonly string _filePath;
    private readonly R2Storage? _r2;
    private readonly object _lock = new();
    private readonly List<Share> _shares = new();
    private long _revision;
    private long _uploadedRevision;

    private ShareStore(string directory, R2Storage? r2)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "shares.json");
        _r2 = r2;
    }

    public static async Task<ShareStore> CreateAsync(string directory, R2Storage? r2, CancellationToken ct)
    {
        var store = new ShareStore(directory, r2);
        await store.LoadAsync(ct);
        return store;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // Local file wins over the R2 mirror because it may be newer.
        if (File.Exists(_filePath))
        {
            _shares.AddRange(Parse(File.ReadAllText(_filePath)));
            return;
        }

        // No local copy (e.g. fresh ephemeral filesystem): pull the mirror.
        if (_r2 is not null && _r2.IsConfigured)
        {
            var remote = await _r2.GetTextAsync(MetaKey, ct);
            if (!string.IsNullOrWhiteSpace(remote))
            {
                _shares.AddRange(Parse(remote));
                SaveLocal();
            }
        }
    }

    public IReadOnlyList<Share> All()
    {
        lock (_lock)
        {
            return _shares.ToList();
        }
    }

    public Share? Get(string id)
    {
        lock (_lock)
        {
            return _shares.FirstOrDefault(s => s.Id == id);
        }
    }

    public async Task<Share> CreateAsync(string name, CancellationToken ct)
    {
        Share share;
        lock (_lock)
        {
            share = new Share
            {
                Id = NewId(),
                Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim(),
                CreatedAtUtc = DateTime.UtcNow
            };
            // Collisions are astronomically unlikely, but keep the invariant anyway.
            if (_shares.Any(s => s.Id == share.Id)) share.Id = NewId();

            _shares.Add(share);
            _revision++;
            SaveLocal();
        }

        // Fire-and-forget the R2 mirror is NOT what we want here: on Render the
        // local file is ephemeral, so make sure the mirror is written before
        // the admin sees the new share.
        await PushToR2Async(ct);
        return share;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        bool removed;
        lock (_lock)
        {
            removed = _shares.RemoveAll(s => s.Id == id) > 0;
            if (removed)
            {
                _revision++;
                SaveLocal();
            }
        }

        if (removed) await PushToR2Async(ct);
        return removed;
    }

    private async Task PushToR2Async(CancellationToken ct)
    {
        if (_r2 is null || !_r2.IsConfigured) return;

        string json;
        long rev;
        lock (_lock)
        {
            // Skip the R2 write if nothing changed since the last upload.
            if (_revision <= _uploadedRevision) return;
            json = Serialize();
            rev = _revision;
        }

        await _r2.PutTextAsync(MetaKey, json, ct);

        // Advance the uploaded revision only on success, so a failed upload is
        // retried by the next mutation instead of being silently skipped.
        lock (_lock)
        {
            if (rev > _uploadedRevision) _uploadedRevision = rev;
        }
    }

    private string Serialize()
    {
        return JsonSerializer.Serialize(_shares, new JsonSerializerOptions { WriteIndented = true });
    }

    private void SaveLocal()
    {
        // Write to a temp file then move, so a crash mid-write can't corrupt
        // the persisted share index.
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, Serialize());
        File.Move(temp, _filePath, overwrite: true);
    }

    private static List<Share> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Share>>(json) ?? new List<Share>();
        }
        catch
        {
            return new List<Share>();
        }
    }

    private static string NewId()
    {
        // 16 random bytes as a URL-safe base64 string (no padding),
        // like YouTube's video IDs but shorter.
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}