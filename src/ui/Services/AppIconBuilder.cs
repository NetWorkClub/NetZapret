using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NetZapret.UI.Services;

public static class AppIconBuilder
{
    private static readonly Uri CustomIconUri = new("avares://NetZapret/Assets/zapret.ico");

    public static WindowIcon CreateShieldIcon(bool isActive)
    {
        try
        {
            if (AssetLoader.Exists(CustomIconUri))
            {
                using var stream = AssetLoader.Open(CustomIconUri);
                return new WindowIcon(stream);
            }
        }
        catch
        {
        }

        const int size = 32;
        var pixels = new byte[size * size * 4];

        byte bgB = isActive ? (byte)129 : (byte)246;
        byte bgG = isActive ? (byte)185 : (byte)130;
        byte bgR = isActive ? (byte)16 : (byte)59;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var idx = (y * size + x) * 4;
                var dx = Math.Abs(x - 15.5);
                var dy = Math.Abs(y - 15.5);
                var inRoundedBox = dx <= 13.5 && dy <= 13.5 && !(dx > 10.5 && dy > 10.5 && (dx - 10.5) * (dx - 10.5) + (dy - 10.5) * (dy - 10.5) > 9.0);

                if (!inRoundedBox)
                    continue;

                var isZ =
                    (y >= 8 && y <= 10 && x >= 9 && x <= 22) ||
                    (y >= 21 && y <= 23 && x >= 9 && x <= 22) ||
                    (y > 10 && y < 21 && Math.Abs((x - 22) + (y - 9) * 1.0) <= 1.8);

                if (isZ)
                {
                    pixels[idx] = 255;
                    pixels[idx + 1] = 255;
                    pixels[idx + 2] = 255;
                    pixels[idx + 3] = 255;
                }
                else
                {
                    pixels[idx] = bgB;
                    pixels[idx + 1] = bgG;
                    pixels[idx + 2] = bgR;
                    pixels[idx + 3] = 255;
                }
            }
        }

        using var wb = new WriteableBitmap(
            new PixelSize(size, size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = wb.Lock())
        {
            for (var y = 0; y < size; y++)
                Marshal.Copy(pixels, y * size * 4, fb.Address + y * fb.RowBytes, size * 4);
        }

        using var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}