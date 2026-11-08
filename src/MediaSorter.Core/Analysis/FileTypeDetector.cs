using System.Buffers.Binary;
using System.Text;

namespace MediaSorter.Core.Analysis;

public enum MediaKind
{
    Unknown,
    Photo,
    Video
}

public sealed record FileType(MediaKind Kind, string Format, string Extension)
{
    public static readonly FileType Unknown = new(MediaKind.Unknown, "Unknown", string.Empty);

    public bool IsIsoBmff => Format is "MP4" or "MOV" or "3GP" or "M4V" or "HEIC" or "AVIF" or "CR3";
}

public static class FileTypeDetector
{
    private const int ProbeSize = 600;

    private static readonly string[] RawTiffExtensions = [".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2", ".pef", ".srw", ".erf", ".kdc", ".3fr"];

    private static readonly Dictionary<string, string[]> EquivalentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = [".jpg", ".jpeg", ".jpe", ".jfif"],
        [".tif"] = [".tif", ".tiff"],
        [".mpg"] = [".mpg", ".mpeg", ".vob"],
        [".mp4"] = [".mp4", ".m4v"],
        [".mov"] = [".mov", ".qt"],
        [".3gp"] = [".3gp", ".3g2", ".3gpp"],
        [".heic"] = [".heic", ".heif", ".hif"],
        [".m2ts"] = [".m2ts", ".mts"],
        [".wmv"] = [".wmv", ".asf"]
    };

    public static FileType Detect(string path)
    {
        Span<byte> buffer = stackalloc byte[ProbeSize];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None))
            read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var type = Detect(buffer[..read]);
        if (type.Format == "TIFF")
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (RawTiffExtensions.Contains(ext))
                return type with { Format = "RAW", Extension = ext };
        }
        return type;
    }

    public static FileType Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4)
            return FileType.Unknown;

        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
            return new(MediaKind.Photo, "JPEG", ".jpg");
        if (h.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return new(MediaKind.Photo, "PNG", ".png");
        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
            return new(MediaKind.Photo, "GIF", ".gif");
        if (h.Length >= 12 && h.StartsWith("RIFF"u8))
        {
            if (h[8..12].SequenceEqual("WEBP"u8)) return new(MediaKind.Photo, "WEBP", ".webp");
            if (h[8..12].SequenceEqual("AVI "u8)) return new(MediaKind.Video, "AVI", ".avi");
            return FileType.Unknown;
        }
        if (h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8))
        {
            if (h.Length >= 10 && h[8] == (byte)'C' && h[9] == (byte)'R')
                return new(MediaKind.Photo, "RAW", ".cr2");
            return new(MediaKind.Photo, "TIFF", ".tif");
        }
        if (h.StartsWith("FUJIFILMCCD-RAW"u8))
            return new(MediaKind.Photo, "RAW", ".raf");
        if (h.Length >= 26 && h[0] == (byte)'B' && h[1] == (byte)'M' && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) is 12 or 40 or 52 or 56 or 108 or 124)
            return new(MediaKind.Photo, "BMP", ".bmp");

        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
            return DetectFtyp(h);
        if (h.Length >= 8 && IsClassicQuickTimeAtom(h[4..8]))
            return new(MediaKind.Video, "MOV", ".mov");

        if (h.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            bool webm = h.IndexOf("webm"u8) >= 0;
            return new(MediaKind.Video, webm ? "WEBM" : "MKV", webm ? ".webm" : ".mkv");
        }
        if (h.StartsWith((ReadOnlySpan<byte>)[0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11]))
            return new(MediaKind.Video, "ASF", ".wmv");
        if (h.StartsWith("FLV"u8))
            return new(MediaKind.Video, "FLV", ".flv");
        if (h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0xBA]) || h.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0xB3]))
            return new(MediaKind.Video, "MPEG-PS", ".mpg");
        if (h.Length >= 3 * 188 && h[0] == 0x47 && h[188] == 0x47 && h[376] == 0x47)
            return new(MediaKind.Video, "MPEG-TS", ".ts");
        if (h.Length >= 3 * 192 && h[4] == 0x47 && h[196] == 0x47 && h[388] == 0x47)
            return new(MediaKind.Video, "M2TS", ".m2ts");

        return FileType.Unknown;
    }

    public static bool ExtensionMatches(string path, FileType type)
    {
        if (type.Kind == MediaKind.Unknown || string.IsNullOrEmpty(type.Extension))
            return true;
        var ext = Path.GetExtension(path);
        if (ext.Equals(type.Extension, StringComparison.OrdinalIgnoreCase))
            return true;
        return EquivalentExtensions.TryGetValue(type.Extension, out var group)
               && group.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static FileType DetectFtyp(ReadOnlySpan<byte> h)
    {
        var major = Encoding.ASCII.GetString(h[8..12]);
        int boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(h), (uint)h.Length);
        var brands = new List<string> { major };
        for (int i = 16; i + 4 <= boxSize; i += 4)
            brands.Add(Encoding.ASCII.GetString(h[i..(i + 4)]));

        if (brands.Any(b => b is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis") || major is "mif1" or "msf1")
            return new(MediaKind.Photo, "HEIC", ".heic");
        if (major is "avif" or "avis")
            return new(MediaKind.Photo, "AVIF", ".avif");
        if (major == "crx ")
            return new(MediaKind.Photo, "CR3", ".cr3");
        if (major == "qt  ")
            return new(MediaKind.Video, "MOV", ".mov");
        if (major.StartsWith("3g", StringComparison.Ordinal))
            return new(MediaKind.Video, "3GP", ".3gp");
        if (major is "M4A " or "M4B " or "M4P " or "F4A ")
            return new(MediaKind.Unknown, "M4A", ".m4a");
        if (major == "M4V ")
            return new(MediaKind.Video, "M4V", ".m4v");
        return new(MediaKind.Video, "MP4", ".mp4");
    }

    private static bool IsClassicQuickTimeAtom(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("wide"u8) ||
        type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8) || type.SequenceEqual("pnot"u8);
}
