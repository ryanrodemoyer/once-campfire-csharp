namespace Campfire.RichText.Fuzz;

/// <summary>The generator: reproducible, and reaching everything it's meant to.</summary>
public class FuzzCorpusTests
{
    static readonly Lazy<OracleRecords> Records = new(() => RecordedCorpus.Load(RecordedCorpus.PathOf("recorded.jsonl.gz")).Records);

    static readonly Lazy<List<FuzzCase>> Sample = new(() => [.. Enumerable.Range(0, 20_000).Select(i => new FuzzCorpus(Records.Value, 7).Case(i))]);

    [Fact]
    public void A_case_is_the_same_every_time_it_is_generated()
    {
        var corpus = new FuzzCorpus(Records.Value, 7);

        Assert.Equal(Sample.Value[12_345], corpus.Case(12_345));
        Assert.NotEqual(Sample.Value[12_345].Body, new FuzzCorpus(Records.Value, 8).Case(12_345).Body);
    }

    [Fact]
    public void Every_family_and_host_is_generated()
    {
        Assert.Equal(FuzzCorpus.Families.Order(), Sample.Value.Select(c => c.Family).Distinct().Order());
        Assert.Equal(FuzzCorpus.Hosts.Distinct().Order(), Sample.Value.Select(c => c.Host).Distinct().Order());
    }

    [Fact]
    public void Every_sgid_variant_the_oracle_minted_is_used()
    {
        var bodies = string.Join("\n", Sample.Value.Where(c => c.Family == "sgid").Select(c => c.Body));

        Assert.Equal([], Records.Value.Sgids.Where(s => !bodies.Contains(Campfire.RailsCompat.Ruby.RubyEscape.HtmlEscape(s.Sgid), StringComparison.Ordinal)).Select(s => s.Label));
    }

    [Fact]
    public void Carries_the_whole_owasp_corpus()
    {
        Assert.Equal(146, FuzzCorpus.Owasp.Count);
        Assert.Contains(FuzzCorpus.Owasp, v => v.Contains("<SCRIPT SRC=", StringComparison.Ordinal));
    }

    [Fact]
    public void Generates_huge_bodies_and_bodies_at_the_parsers_limits()
    {
        var huge = Sample.Value.Where(c => c.Family == "huge").ToList();

        Assert.Contains(huge, c => c.Body.Length > 256 * 1024);
        Assert.Contains(Sample.Value, c => c.Family == "nesting" && c.Body.Split("<").Length > 400);
    }

    [Fact]
    public void Bodies_are_valid_unicode()
    {
        Assert.All(Sample.Value, c => Assert.Equal(-1, IndexOfLoneSurrogate(c.Body)));
    }

    static int IndexOfLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
