using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace PalmierShell.Core;

/// Converts raw BGRA thumbnail tiles from the core into Avalonia bitmaps.
public static class ThumbnailBitmaps {
    /// Wraps tile `index` of a packed 96x54 BGRA tile buffer as a Bitmap.
    public static Bitmap FromTiles(byte[] tiles, int index) {
        int w = CoreApi.ThumbTileWidth, h = CoreApi.ThumbTileHeight;
        int tileBytes = w * h * 4;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock()) {
            var src = tiles.AsSpan(index * tileBytes, tileBytes);
            if (fb.RowBytes == w * 4) {
                Marshal.Copy(tiles, index * tileBytes, fb.Address, tileBytes);
            } else {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(tiles, index * tileBytes + y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }
        }
        return bmp;
    }
}
