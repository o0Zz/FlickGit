using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// An <c>.ico</c> file as a menu bitmap.
///
/// <b>A registry verb gets to name an icon file; a menu item does not.</b> <c>InsertMenu</c> takes
/// text and nothing else, so an <c>IContextMenu</c> handler has to hand the shell an <c>HBITMAP</c>
/// through <c>SetMenuItemInfo</c> — which means loading the icon and drawing it into a bitmap
/// ourselves. This is the price of the placement, and it is why TortoiseGit's shell extension
/// carries the same code.
///
/// <b>32-bit top-down DIB, not <c>CopyImage</c>.</b> A menu bitmap without an alpha channel renders
/// its transparent pixels as black squares, which is worse than no icon. Drawing the icon into a
/// <c>CreateDIBSection</c> surface with <c>DrawIconEx</c> preserves the alpha the <c>.ico</c> came
/// with.
///
/// Bitmaps are cached and never freed. There are at most a handful, they live as long as the process,
/// and a bitmap freed while a menu still references it is a GDI fault inside
/// <c>explorer.exe</c> — the one outcome worth strictly more than the handles.
/// </summary>
internal static unsafe partial class MenuIcons
{
    private static readonly Dictionary<string, nint> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// The bitmap for <paramref name="iconPath"/>, or zero when it cannot be produced.
    ///
    /// Zero is a normal answer, not a failure to report: the item is then drawn without an icon,
    /// which is exactly what a registry verb pointing at a missing file also does.
    /// </summary>
    public static nint Bitmap(string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath))
            return 0;

        lock (Gate)
        {
            if (Cache.TryGetValue(iconPath, out nint cached))
                return cached;

            nint bitmap = Create(iconPath);
            Cache[iconPath] = bitmap;
            return bitmap;
        }
    }

    private static nint Create(string iconPath)
    {
        //The size the shell draws a menu check/icon at. Asking for it rather than assuming 16 is what
        //makes the icon sharp at 150% and 200% scaling.
        int width = GetSystemMetrics(SM_CXSMICON);
        int height = GetSystemMetrics(SM_CYSMICON);

        if (width <= 0 || height <= 0)
            return 0;

        nint icon = LoadImageW(0, iconPath, ImageIcon, width, height, LoadFromFile);

        if (icon == 0)
            return 0;

        nint screen = 0;
        nint memory = 0;
        nint bitmap = 0;
        nint previous = 0;

        try
        {
            var header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,

                //Negative: a top-down DIB. A bottom-up one draws the icon upside down.
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            screen = GetDC(0);
            bitmap = CreateDIBSection(screen, &header, DibRgbColors, out _, 0, 0);

            if (bitmap == 0)
                return 0;

            memory = CreateCompatibleDC(screen);
            previous = SelectObject(memory, bitmap);

            //DI_NORMAL draws image and mask together, which is what carries the alpha across.
            if (!DrawIconEx(memory, 0, 0, icon, width, height, 0, 0, DiNormal))
            {
                DeleteObject(bitmap);
                return 0;
            }

            nint drawn = bitmap;

            //Cleared so the finally below does not delete what is being returned.
            bitmap = 0;
            return drawn;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (previous != 0)
                SelectObject(memory, previous);

            if (memory != 0)
                DeleteDC(memory);

            if (screen != 0)
                ReleaseDC(0, screen);

            if (bitmap != 0)
                DeleteObject(bitmap);

            DestroyIcon(icon);
        }
    }

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DibRgbColors = 0;
    private const uint DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(
        nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(
        nint dc, BitmapInfoHeader* header, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint gdiObject);
}
