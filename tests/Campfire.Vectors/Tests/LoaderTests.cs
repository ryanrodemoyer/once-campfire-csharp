using System.Reflection;
using System.Text;

namespace Campfire.Vectors.Tests;

public sealed class LoaderTests
{
    static readonly Type[] Families =
    [
        typeof(RailsCompatVectors), typeof(RubyCoreVectors), typeof(CampfireVectors), typeof(StorageVectors),
        typeof(RichTextVectors), typeof(IntegrationVectors), typeof(QrCodeVectors), typeof(Manifest),
    ];

    public static TheoryData<string> TheoryDataMembers => new(
        Families.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(TheoryData<>))
            .Select(m => $"{type.Name}.{m.Name}")));

    // Every family the loader exposes parses with every field accounted for and yields named rows.
    [Theory]
    [MemberData(nameof(TheoryDataMembers))]
    public void EveryMemberYieldsNamedRows(string member)
    {
        var (typeName, methodName) = (member.Split('.')[0], member.Split('.')[1]);
        var method = Families.Single(t => t.Name == typeName).GetMethod(methodName)!;
        var rows = ((IEnumerable<ITheoryDataRow>)method.Invoke(null, null)!).ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.TestDisplayName));
            Assert.NotNull(Assert.Single(row.GetData()));
        });
    }

    [Theory]
    [InlineData("""{"salt": "s", "length": 64, "key_hex": "00", "extra": 1}""")]
    [InlineData("""{"salt": "s", "length": 64}""")]
    [InlineData("""{"salt": null, "length": 64, "key_hex": "00"}""")]
    public void LoaderRejectsVectorsThatChangedShape(string json)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => VectorFiles.Parse<KeyGeneratorCase>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void RubyResultReadsValuesAndRaisedErrors()
    {
        var ok = VectorFiles.Parse<RubyResult<string?>>("\"Chrome\""u8);
        var none = VectorFiles.Parse<RubyResult<string?>>("null"u8);
        var raised = VectorFiles.Parse<RubyResult<bool>>("""{"error": "NoMethodError"}"""u8);

        Assert.Equal(new RubyResult<string?>("Chrome", null), ok);
        Assert.Equal(new RubyResult<string?>(null, null), none);
        Assert.True(raised.Raised);
        Assert.Equal("NoMethodError", raised.Error);
    }

    [Fact]
    public void FamiliesHaveTheReferenceCaseCounts()
    {
        Assert.Equal(385, CampfireVectors.UserAgentsFile.UserAgents.Count);
        Assert.Equal(177, CampfireVectors.RoutesFile.Routes.Count);
        Assert.Equal(111, CampfireVectors.RoutesFile.Recognitions.Count);
        Assert.Equal(90, IntegrationVectors.OpenGraphExpectedFile.Count);
        Assert.Equal(90, IntegrationVectors.OpenGraphCasesFile.Cases.Count);
        Assert.Equal(19, IntegrationVectors.WebhookExpectedFile.Count);
        Assert.Equal(19, IntegrationVectors.WebhookCasesFile.Count);
        Assert.Equal(658, RichTextVectors.File.Cases.Count);
        Assert.Equal(39, QrCodeVectors.File.Count);
        Assert.Equal(427, RubyCoreVectors.File.Strings.Count);
        Assert.Equal(189, RailsCompatVectors.File.Csrf.Validity.Count);
    }

    [Theory]
    [MemberData(nameof(CampfireVectors.UserAgents), MemberType = typeof(CampfireVectors))]
    public void UserAgentResultsAreValuesOrRaisedErrors(UserAgentCase vector)
    {
        Assert.True(vector.Mobile.Raised || vector.Ua is not null || !vector.Mobile.Value);
        if (vector.Version.Raised)
        {
            Assert.Null(vector.Version.Value);
            Assert.EndsWith("Error", vector.Version.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(RichTextVectors.Cases), MemberType = typeof(RichTextVectors))]
    public void RichTextOutcomesAreOkOrRaised(RichTextCase vector)
    {
        AssertOkOrRaised(vector.Presentation);
        AssertOkOrRaised(vector.PlainText);
        AssertOkOrRaised(vector.Editable);
        AssertOkOrRaised(vector.Mentioned);
        AssertOkOrRaised(vector.Filtered);
        Assert.Equal(vector.PresentationRaised is null, vector.PresentationRaisedMessage is null);
    }

    [Theory]
    [MemberData(nameof(StorageVectors.Variants), MemberType = typeof(StorageVectors))]
    public void VariantFilesMatchTheirBlobs(StoredVariant variant)
    {
        var bytes = StorageVectors.ReadFile(variant.File);

        Assert.Equal(variant.Blob.ByteSize, bytes.LongLength);
        // Active Storage checksums are base64 MD5 digests.
#pragma warning disable CA5351
        Assert.Equal(variant.Blob.Checksum, Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(bytes)));
#pragma warning restore CA5351
    }

    [Theory]
    [MemberData(nameof(IntegrationVectors.Webhook), MemberType = typeof(IntegrationVectors))]
    public void WebhookCasesPairWithTheirScripts(WebhookExpectedCase vector)
    {
        Assert.Contains(IntegrationVectors.WebhookCasesFile, c => c.Name == vector.Name);
        if (vector.Reply?.TextB64 is { } text)
        {
            Assert.NotNull(Encoding.Latin1.GetString(Convert.FromBase64String(text)));
        }
    }

    static void AssertOkOrRaised<T>(Outcome<T> outcome)
    {
        if (outcome.Raised)
        {
            Assert.Null(outcome.Ok);
            Assert.NotNull(outcome.Message);
        }
        else
        {
            Assert.Null(outcome.Message);
        }
    }
}
