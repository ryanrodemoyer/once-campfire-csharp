namespace Campfire.Vectors;

// vectors/ruby_core.json, written by reference-tools/ruby_core.rb: Ruby string coercion and
// escaping, Float#to_s, and Rack byte-range parsing.

public sealed record RubyCoreFile(
    RubyCoreVersions Versions,
    IReadOnlyList<RubyStringCase> Strings,
    IReadOnlyList<RubyFloatCase> Floats,
    IReadOnlyList<ByteRangeCase> ByteRanges);

public sealed record RubyCoreVersions(string Ruby, string Rack, string Rails, string Addressable);

public sealed record RubyStringCase(
    string Input,
    string ToI,
    string? IntegerCast,
    string ToF,
    string Strip,
    string HtmlEscape,
    string CgiEscape,
    string UrlEncode,
    string AddressableUnreserved,
    string RackEscape);

/// <summary><c>Bits</c> is the IEEE 754 double as 16 hex digits.</summary>
public sealed record RubyFloatCase(string Bits, string ToS);

/// <summary><c>Ranges</c> is Rack's inclusive [first, last] pairs, or null when unsatisfiable.</summary>
public sealed record ByteRangeCase(string? Header, long Size, IReadOnlyList<IReadOnlyList<long>>? Ranges);

public static class RubyCoreVectors
{
    static readonly Lazy<RubyCoreFile> Data = new(() => VectorFiles.Load<RubyCoreFile>("ruby_core.json"));

    public static RubyCoreFile File => Data.Value;

    public static TheoryData<RubyStringCase> Strings() => VectorFiles.Rows(File.Strings);
    public static TheoryData<RubyFloatCase> Floats() => VectorFiles.Rows(File.Floats, c => c.Bits);
    public static TheoryData<ByteRangeCase> ByteRanges() => VectorFiles.Rows(File.ByteRanges);
}
