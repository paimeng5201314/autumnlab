using System.Buffers.Binary;

namespace AutumnOS.NativeHost.Windows;

// A finite render extent. Resizing changes the aspect ratio, not the decoded-frame budget.
public readonly record struct NativeViewport
{
    public const int MaxPixels = 800 * 600;
    public const int MaxEdge = 2048;
    public static NativeViewport Default { get; } = new(800, 600);
    public int Width { get; }
    public int Height { get; }
    public bool IsValid => Width is >= 1 and <= MaxEdge && Height is >= 1 and <= MaxEdge &&
        (long)Width * Height <= MaxPixels;

    public NativeViewport(int width, int height)
    {
        Width = width; Height = height;
        Validate();
    }

    public void Validate()
    {
        if (!IsValid) throw new InvalidDataException("NATIVE_VIEWPORT_INVALID");
    }

    public static NativeViewport FromSurface(double width, double height)
    {
        if (!double.IsFinite(width) || width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        // Apply the edge bound before multiplying; even finite dimensions can overflow their product.
        double edgeScale = Math.Min(1, MaxEdge / Math.Max(width, height));
        double w = width * edgeScale, h = height * edgeScale;
        double pixelScale = Math.Min(1, Math.Sqrt(MaxPixels / (w * h)));
        return new(Math.Max(1, (int)Math.Floor(w * pixelScale)), Math.Max(1, (int)Math.Floor(h * pixelScale)));
    }

    public static NativeViewport Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8) throw new InvalidDataException("NATIVE_VIEWPORT_INVALID");
        return new(BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
    }

    public byte[] Encode()
    {
        Validate();
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload, Width);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), Height);
        return payload;
    }

    public bool TryMap(double surfaceWidth, double surfaceHeight, double px, double py,
        out int x, out int y, bool clamp = false)
    {
        x = y = 0;
        if (!IsValid || !double.IsFinite(surfaceWidth) || !double.IsFinite(surfaceHeight) ||
            surfaceWidth < 1 || surfaceHeight < 1 || !double.IsFinite(px) || !double.IsFinite(py)) return false;
        double scale = Math.Min(surfaceWidth / Width, surfaceHeight / Height);
        double localX = (px - (surfaceWidth - Width * scale) / 2) / scale;
        double localY = (py - (surfaceHeight - Height * scale) / 2) / scale;
        if (!double.IsFinite(localX) || !double.IsFinite(localY)) return false;
        if (!clamp && (localX < 0 || localY < 0 || localX >= Width || localY >= Height)) return false;
        x = (int)Math.Floor(Math.Clamp(localX, 0, Width - 1));
        y = (int)Math.Floor(Math.Clamp(localY, 0, Height - 1));
        return true;
    }
}
