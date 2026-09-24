using QRCoder;

namespace FlickThem.Services;

public sealed class QrCodeService
{
    // Renders the given payload (the share's upload URL) as an SVG QR code
    // so it can be shown on screen or downloaded and printed.
    public string Svg(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(20);
        return svg;
    }
}