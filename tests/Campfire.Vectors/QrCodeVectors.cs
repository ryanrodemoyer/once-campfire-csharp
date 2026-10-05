namespace Campfire.Vectors;

/// <summary>
/// One RQRCode result from reference-tools/campfire/rqrcode.rb: the input, the QR version, the
/// module matrix as rows of 0/1, and the SVG QrCodeController renders.
/// </summary>
public sealed record QrCodeCase(string InputBase64, int Version, string Modules, string? Svg = null);

public static class QrCodeVectors
{
    static readonly Lazy<IReadOnlyList<QrCodeCase>> Data = new(() => VectorFiles.Load<IReadOnlyList<QrCodeCase>>("rqrcode.json"));

    public static IReadOnlyList<QrCodeCase> File => Data.Value;

    public static TheoryData<QrCodeCase> Cases() => VectorFiles.Rows(File);
}
