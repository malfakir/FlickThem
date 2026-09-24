using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace FlickThem.Services;

public sealed class R2Options
{
    public string AccountId { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Bucket { get; set; } = "";
    // Optional: an account-scoped Cloudflare API token (created under
    // My Profile -> API Tokens) with read access to R2 storage. Without it,
    // storage-capacity enforcement is skipped.
    public string ApiToken { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(Bucket);
}

public sealed record R2File
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
    public string GetUrl { get; init; } = "";
}

public sealed class R2Storage
{
    private const string KeyPrefix = "shares";
    private const int MaxGetUrlMinutes = 60;
    private const int UsageCacheSeconds = 60;

    // Shared HTTP client for the Cloudflare REST API (metrics/usage calls).
    private static readonly HttpClient Http = new();

    public bool IsConfigured => _options.IsConfigured;

    private readonly AmazonS3Client? _s3;
    private readonly R2Options _options;
    private readonly string _bucket;

    // Tiny in-memory cache so we query the Cloudflare usage API at most once a minute.
    private readonly object _usageLock = new();
    private long? _usageCache;
    private DateTime _usageCacheAt;

    public R2Storage(IOptions<R2Options> options)
    {
        _options = options.Value;
        _bucket = _options.Bucket;

        if (_options.IsConfigured)
        {
            // R2 is S3-compatible: point the AWS SDK at Cloudflare's endpoint.
            // AuthenticationRegion "auto" is what Cloudflare expects for R2.
            var config = new AmazonS3Config
            {
                ServiceURL = $"https://{_options.AccountId}.r2.cloudflarestorage.com",
                AuthenticationRegion = "auto",
                ForcePathStyle = true
            };

            _s3 = new AmazonS3Client(_options.AccessKeyId, _options.SecretAccessKey, config);
        }
    }

    private AmazonS3Client Client()
    {
        if (!_options.IsConfigured || _s3 is null)
            throw new InvalidOperationException(
                "R2 is not configured. Set R2:AccountId, R2:AccessKeyId, R2:SecretAccessKey and R2:Bucket.");
        return _s3;
    }

    /// <summary>
    /// Current total bytes stored in the bucket, via the Cloudflare R2 metrics
    /// API. Returns null when no ApiToken is configured or the API is
    /// unreachable (in which case capacity checks are skipped, not failed).
    /// </summary>
    public async Task<long?> GetUsageBytesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiToken)) return null;

        lock (_usageLock)
        {
            if (_usageCache.HasValue && _usageCacheAt > DateTime.UtcNow.AddSeconds(-UsageCacheSeconds))
                return _usageCache;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.cloudflare.com/client/v4/accounts/{_options.AccountId}/r2/metrics");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiToken);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            long total = 0;
            foreach (var group in new[] { "standard", "infrequentAccess" })
            {
                if (!doc.RootElement.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty(group, out var cls)) continue;

                foreach (var state in new[] { "uploaded", "published" })
                {
                    if (!cls.TryGetProperty(state, out var st)) continue;
                    total += st.TryGetProperty("payloadSize", out var payload) && payload.ValueKind == JsonValueKind.Number ? payload.GetInt64() : 0;
                    total += st.TryGetProperty("metadataSize", out var meta) && meta.ValueKind == JsonValueKind.Number ? meta.GetInt64() : 0;
                }
            }

            lock (_usageLock)
            {
                _usageCache = total;
                _usageCacheAt = DateTime.UtcNow;
            }
            return total;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildObjectKey(string shareId, string fileName)
    {
        // Object layout: shares/{shareId}/{guid}-{sanitizedFileName}
        // The guid ensures filenames never collide between visitors.
        return $"{KeyPrefix}/{shareId}/{Guid.NewGuid():N}-{SanitizeName(fileName)}";
    }

    public static string SanitizeName(string fileName)
    {
        // Strip path components and any characters that are awkward in object keys.
        var name = Path.GetFileName(fileName).Trim();
        if (name.Length > 80) name = name[..80];

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var ok = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ' ' || c == '(' || c == ')';
            if (!ok) chars[i] = '_';
        }

        return new string(chars).Replace("__", "_");
    }

    public static string ObjectNameFromKey(string key)
    {
        // Reverse of BuildObjectKey: turn an object key back into the original
        // filename so the gallery can display friendly names.
        var separator = "/";
        var idx = key.LastIndexOf(separator, StringComparison.Ordinal);
        if (idx < 0) return key;

        var tail = key[(idx + 1)..];
        var dash = tail.IndexOf('-');
        return dash > 0 ? tail[(dash + 1)..] : tail;
    }

    public string CreatePutUrl(string objectKey, TimeSpan ttl)
    {
        // Pre-signed PUT: lets the browser upload directly to R2 for up to ttl,
        // without needing credentials. Only this exact object can be written.
        return Client().GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(ttl)
        });
    }

    public string CreateGetUrl(string objectKey)
    {
        // Pre-signed GET for viewing/downloading a single object.
        return Client().GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(MaxGetUrlMinutes)
        });
    }

    public async Task<IReadOnlyList<R2File>> ListFilesAsync(string shareId, CancellationToken ct)
    {
        // List all objects under the share's prefix, handling R2 pagination.
        var prefix = $"{KeyPrefix}/{shareId}/";
        var files = new List<R2File>();
        var continuation = "";

        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix
            };
            // R2 (like S3) rejects an empty continuation token, so only set it
            // once we actually have one from a truncated listing.
            if (continuation != "") request.ContinuationToken = continuation;

            var response = await Client().ListObjectsV2Async(request, ct);

            foreach (var obj in response.S3Objects)
            {
                files.Add(new R2File
                {
                    Key = obj.Key,
                    Name = ObjectNameFromKey(obj.Key),
                    Size = obj.Size ?? 0,
                    LastModifiedUtc = obj.LastModified,
                    GetUrl = CreateGetUrl(obj.Key)
                });
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : "";
        } while (continuation != "");

        return files;
    }

    public async Task<string?> GetTextAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await Client().GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct);

            using var stream = response.ResponseStream;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutTextAsync(string key, string content, CancellationToken ct)
    {
        await Client().PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentType = "application/json",
            ContentBody = content,
            // R2 does not support streaming (chunked) SigV4 payload signing,
            // so upload with a known-length body and a plain signature instead.
            UseChunkEncoding = false
        }, ct);
    }

    public async Task DeleteShareAsync(string shareId, CancellationToken ct)
    {
        // Remove every object that belongs to a share, in batches of 1000.
        var prefix = $"{KeyPrefix}/{shareId}/";

        while (true)
        {
            var list = await Client().ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                MaxKeys = 1000
            }, ct);

            if (list.S3Objects.Count == 0) break;

            await Client().DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _bucket,
                Objects = list.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList()
            }, ct);

            if (list.IsTruncated != true) break;
        }
    }
}
