using System.IO.Compression;
using ZstdSharp;

const string Magic = "UEFORMAT";
var additiveNames = new Dictionary<int, string>
{
    [0] = "AAT_None",
    [1] = "AAT_LocalSpaceBase",
    [2] = "AAT_RotationOffsetMeshSpace",
};
var refNames = new Dictionary<int, string>
{
    [0] = "ABPT_None",
    [1] = "ABPT_RefPose",
    [2] = "ABPT_AnimScaled",
    [3] = "ABPT_AnimFrame",
    [4] = "ABPT_LocalAnimFrame",
};

var files = new List<string>();
string? dir = null;
var needles = new List<string>();
var expected = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--dir" && i + 1 < args.Length) dir = args[++i];
    else if (args[i] == "--name" && i + 1 < args.Length)
        needles.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else if (args[i] == "--expect-curve" && i + 1 < args.Length)
        expected.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else files.Add(args[i]);
}

if (dir != null)
{
    foreach (var path in Directory.EnumerateFiles(dir, "*.ueanim", SearchOption.AllDirectories))
    {
        if (needles.Count == 0 || needles.Any(needle => Path.GetFileName(path).Contains(needle, StringComparison.OrdinalIgnoreCase)))
            files.Add(path);
    }
}

var failed = 0;
foreach (var path in files)
{
    var info = Summarize(path);
    var curveText = info.Curves.Count == 0
        ? "-"
        : string.Join(", ", info.Curves.Select(curve => $"{curve.Name}:{curve.Keys.Count}"));
    Console.WriteLine($"{Path.GetFileName(path)}  additive={info.Additive}  ref={info.RefType}  frame={info.RefFrame}  tracks={info.Tracks}  curves={info.Curves.Count}  [{curveText}]");
    Console.WriteLine($"  refPose={info.RefPose}");
    var present = info.Curves.Select(curve => curve.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var name in expected)
    {
        if (!present.Contains(name))
        {
            Console.WriteLine($"  MISSING {name}");
            failed++;
        }
    }
}

return failed == 0 ? 0 : 1;

Summary Summarize(string path)
{
    var body = DecompressBody(File.ReadAllBytes(path));
    var attributes = ParseAttributes(body);
    var meta = new Reader(attributes["METADATA"]);
    meta.I32();
    meta.F32();
    var refPose = meta.String();
    var additive = meta.U8();
    var refType = meta.U8();
    var refFrame = meta.I32();
    var curves = new List<Curve>();
    if (attributes.TryGetValue("CURVES", out var curveBytes))
    {
        var curveReader = new Reader(curveBytes);
        var count = curveReader.I32();
        for (var i = 0; i < count; i++)
        {
            var name = curveReader.String();
            var keyCount = curveReader.I32();
            var keys = new List<(int Frame, float Value)>(keyCount);
            for (var key = 0; key < keyCount; key++)
                keys.Add((curveReader.I32(), curveReader.F32()));
            curves.Add(new Curve(name, keys));
        }
    }

    var tracks = attributes.TryGetValue("TRACKS", out var trackBytes) ? new Reader(trackBytes).I32() : 0;
    return new Summary(
        Additive: additiveNames.GetValueOrDefault(additive, additive.ToString()),
        RefType: refNames.GetValueOrDefault(refType, refType.ToString()),
        RefFrame: refFrame,
        RefPose: string.IsNullOrEmpty(refPose) ? "-" : refPose,
        Tracks: tracks,
        Curves: curves);
}

byte[] DecompressBody(byte[] file)
{
    var reader = new Reader(file);
    var magic = reader.Take(Magic.Length);
    if (System.Text.Encoding.ASCII.GetString(magic) != Magic)
        throw new InvalidDataException("not a UEFORMAT file");
    reader.String();
    reader.U8();
    reader.String();
    reader.String();
    var compressed = reader.U8() != 0;
    if (!compressed)
        return reader.Rest();

    var format = reader.String();
    reader.I32();
    var compressedSize = reader.I32();
    var blob = reader.Take(compressedSize);
    if (format == "ZSTD")
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(blob).ToArray();
    }
    if (format == "GZIP")
        return gzipUnwrap(blob);
    return blob;

    static byte[] gzipUnwrap(byte[] src)
    {
        using var input = new MemoryStream(src);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

Dictionary<string, byte[]> ParseAttributes(byte[] body)
{
    var reader = new Reader(body);
    var count = reader.I32();
    var attributes = new Dictionary<string, byte[]>(count);
    for (var i = 0; i < count; i++)
    {
        var name = reader.String();
        var size = reader.I32();
        attributes[name] = reader.Take(size);
    }
    return attributes;
}

sealed class Reader(byte[] data)
{
    private int _offset;

    public int I32()
    {
        var value = BitConverter.ToInt32(data, _offset);
        _offset += 4;
        return value;
    }

    public int U8() => data[_offset++];

    public float F32()
    {
        var value = BitConverter.ToSingle(data, _offset);
        _offset += 4;
        return value;
    }

    public string String()
    {
        var size = I32();
        if (size < 0)
            throw new InvalidDataException($"negative string length {size}");
        var text = System.Text.Encoding.UTF8.GetString(data, _offset, size);
        _offset += size;
        return text;
    }

    public byte[] Take(int size)
    {
        var slice = data[_offset..(_offset + size)];
        _offset += size;
        return slice;
    }

    public byte[] Rest() => data[_offset..];
}

record Curve(string Name, List<(int Frame, float Value)> Keys);
record Summary(string Additive, string RefType, int RefFrame, string RefPose, int Tracks, List<Curve> Curves);
