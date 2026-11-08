using Net.Codecrete.QrCodeGenerator;

namespace NightCodeForge.Web.Tailscale;

/// <summary>
/// Generates self-contained ISO/IEC 18004 SVG QR code strings for offline/air-gapped rendering without external CDNs.
/// </summary>
public static class QrCodeSvgGenerator
{
    private const int DefaultBorder = 2;

    public static string GenerateSvg(string content, int border = DefaultBorder)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var qr = QrCode.EncodeText(content, QrCode.Ecc.Medium);
        return qr.ToSvgString(border);
    }
}

