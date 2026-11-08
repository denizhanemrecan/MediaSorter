using System.Buffers.Binary;
using MediaSorter.Core.Recovery;
using MediaSorter.Core.Tools;

namespace MediaSorter.Core.Analysis;

public enum HealthStatus
{
    Ok,
    Broken
}

public sealed record HealthReport(HealthStatus Status, string Reason)
{
    public static readonly HealthReport Ok = new(HealthStatus.Ok, string.Empty);
    public static HealthReport Broken(string reason) => new(HealthStatus.Broken, reason);
    public bool IsBroken => Status == HealthStatus.Broken;
}

public static class HealthChecker
{
    public static async Task<HealthReport> CheckAsync(string path, FileType type, FfmpegTools? deepVerify, CancellationToken ct)
    {
        try
        {
            var report = type.Format switch
            {
                "JPEG" => CheckJpeg(path),
                "PNG" => CheckPng(path),
                _ when type.IsIsoBmff => CheckIsoBmff(path, type),
                _ => HealthReport.Ok
            };
            if (report.IsBroken || deepVerify is null || type.Kind != MediaKind.Video)
                return report;
            return await deepVerify.ProbeAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return HealthReport.Broken($"unreadable: {ex.Message}");
        }
    }

    private static HealthReport CheckJpeg(string path)
    {
        var data = File.ReadAllBytes(path);
        int p = 2;
        while (p + 4 <= data.Length)
        {
            if (data[p] != 0xFF)
                return HealthReport.Broken($"corrupt JPEG marker at 0x{p:X}");
            byte marker = data[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2));
            if (p + 2 + len > data.Length)
                return HealthReport.Broken("truncated JPEG header");
            if (marker == 0xDA)
            {
                return data.AsSpan(p + 2 + len).IndexOf([(byte)0xFF, (byte)0xD9]) >= 0
                    ? HealthReport.Ok
                    : HealthReport.Broken("truncated JPEG (no end-of-image marker)");
            }
            p += 2 + len;
        }
        return HealthReport.Broken("JPEG has no image data");
    }

    private static HealthReport CheckPng(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> hdr = stackalloc byte[8];
        long p = 8;
        while (p + 12 <= fs.Length)
        {
            fs.Position = p;
            fs.ReadExactly(hdr);
            long len = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            if (hdr[4..8].SequenceEqual("IEND"u8))
                return HealthReport.Ok;
            p += 12 + len;
        }
        return HealthReport.Broken("truncated PNG (no IEND chunk)");
    }

    private static HealthReport CheckIsoBmff(string path, FileType type)
    {
        using var fs = File.OpenRead(path);
        var boxes = IsoBmff.ReadTopLevel(fs);
        var truncated = boxes.FirstOrDefault(b => b.IsTruncated);
        if (truncated is not null)
            return HealthReport.Broken($"truncated: '{truncated.Type}' declares {truncated.DeclaredSize:N0} bytes, file ends {truncated.DeclaredSize - truncated.Size:N0} bytes short");
        string required = type.Kind == MediaKind.Video || type.Format == "CR3" ? "moov" : "meta";
        if (boxes.All(b => b.Type != required))
            return HealthReport.Broken($"missing '{required}' atom");
        return HealthReport.Ok;
    }
}
