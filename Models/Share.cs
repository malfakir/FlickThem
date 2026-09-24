namespace FlickThem.Models;

public sealed class Share
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}