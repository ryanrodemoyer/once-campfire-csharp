using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campfire.Vectors;

// vectors/rails_compat.json, written by reference-tools/rails_compat_vectors.rb: Rails' key
// generator, signed and encrypted cookies, session and CSRF tokens, signed ids, global ids, SGIDs,
// Turbo stream names, app verifiers and bcrypt passwords. Values that Ruby returns in more than one
// shape (a string, an integer, a hash or nil) stay as JsonElement.

public sealed record RailsCompatFile(
    string SecretKeyBase,
    string RotatedSecretKeyBase,
    string Now,
    IReadOnlyList<KeyGeneratorCase> KeyGenerator,
    IReadOnlyList<CookieEscapingCase> CookieEscaping,
    GenerateVerify<SignedCookieGenerate, SignedCookieVerify> SignedCookies,
    GenerateVerify<EncryptedCookieGenerate, EncryptedCookieVerify> EncryptedCookies,
    SessionVector Session,
    CsrfVectors Csrf,
    GenerateVerify<SignedIdGenerate, SignedIdVerify> SignedIds,
    IReadOnlyList<GlobalIdCase> GlobalIds,
    SgidVectors Sgids,
    IReadOnlyList<UnverifiedSgidCase> UnverifiedSgids,
    GenerateVerify<TurboStreamNameGenerate, TurboStreamNameVerify> TurboStreamNames,
    GenerateVerify<AppVerifierGenerate, AppVerifierVerify> AppVerifiers,
    PasswordVectors Passwords);

public sealed record GenerateVerify<TGenerate, TVerify>(IReadOnlyList<TGenerate> Generate, IReadOnlyList<TVerify> Verify);

public sealed record KeyGeneratorCase(string Salt, int Length, string KeyHex);

public sealed record CookieEscapingCase(string? Raw, string Wire, string Parsed);

public sealed record SignedCookieGenerate(string Name, string Value, string? ExpiresAt, string Raw, string SetCookie);

public sealed record SignedCookieVerify(string Case, string Name, string Raw, string Now, JsonElement Expected);

public sealed record EncryptedCookieGenerate(
    string Name, JsonElement Value, string? ExpiresAt, string Raw, string Plaintext, string? SetCookie = null);

public sealed record EncryptedCookieVerify(string Case, string Name, string Raw, string Now, JsonElement Expected);

/// <summary>A real session round trip through the reference app: sign-in page, CSRF checks, login.</summary>
public sealed record SessionVector(
    int NewStatus,
    string SetCookie,
    string SessionCookieRaw,
    IReadOnlyDictionary<string, string> Session,
    string CsrfMetaToken,
    string SessionFormToken,
    int PostWithBadTokenStatus,
    int PostWithFormTokenStatus,
    int PostWithMetaTokenHeaderStatus,
    int PostWithCrossOriginStatus,
    string SessionTokenSetCookie,
    string SessionTokenRaw,
    string SessionTokenValue,
    string SessionAfterLoginRaw,
    IReadOnlyDictionary<string, string> SessionAfterLogin);

public sealed record CsrfVectors(
    string SessionToken,
    string GlobalTokenHex,
    IReadOnlyList<string> GlobalTokens,
    IReadOnlyList<CsrfFormToken> FormTokens,
    IReadOnlyList<CsrfValidityCase> Validity,
    IReadOnlyList<CsrfOriginCase> Origin,
    bool PerFormCsrfTokens,
    bool ForgeryProtectionOriginCheck,
    string GeneratedSessionTokenExample);

public sealed record CsrfFormToken(
    string Action, string Method, string PagePath, string NormalizedActionPath, string UnmaskedHex, string Token);

public sealed record CsrfValidityCase(string Case, string Token, string Path, string Method, bool Expected);

/// <summary><c>Expected</c> is true, false or the string "raises".</summary>
public sealed record CsrfOriginCase(string? Origin, string BaseUrl, JsonElement Expected);

public sealed record SignedIdGenerate(string Model, long Id, string? Purpose, string? ExpiresAt, string SignedId);

public sealed record SignedIdVerify(
    string Case, string Model, string SignedId, string? Purpose, string Now, JsonElement Expected);

public sealed record GlobalIdCase(string ModelName, string Id, string Gid, string Param);

public sealed record SgidVectors(string App, IReadOnlyList<SgidGenerate> Generate, IReadOnlyList<SgidVerify> Verify);

public sealed record SgidGenerate(string Gid, string Data, string Purpose, string? ExpiresAt, string Sgid);

public sealed record SgidVerify(string Case, string Sgid, string Purpose, string Now, string? Expected);

/// <summary><c>Expected</c> is a GID string, null, or <c>{"raises": "..."}</c>.</summary>
public sealed record UnverifiedSgidCase(string Case, string? Sgid, JsonElement Expected);

public sealed record TurboStreamNameGenerate(
    IReadOnlyList<string> Parts, string StreamName, [property: JsonPropertyName("signed")] string SignedName);

public sealed record TurboStreamNameVerify(
    string Case, [property: JsonPropertyName("signed")] string SignedName, JsonElement Expected);

public sealed record AppVerifierGenerate(
    string Name, string DataJson, string? Purpose, string? ExpiresAt, string Message, string? Via = null);

public sealed record AppVerifierVerify(
    string Case, string Name, string Message, string? Purpose, string Now, string? ExpectedJson);

public sealed record PasswordVectors(
    int Cost, IReadOnlyList<PasswordDigest> Digests, IReadOnlyList<PasswordCheck> Checks, string SeededUserDigest);

public sealed record PasswordDigest(string Password, string Digest);

public sealed record PasswordCheck(string Digest, string Password, bool Expected);

public static class RailsCompatVectors
{
    static readonly Lazy<RailsCompatFile> Data = new(() => VectorFiles.Load<RailsCompatFile>("rails_compat.json"));

    public static RailsCompatFile File => Data.Value;

    public static TheoryData<KeyGeneratorCase> KeyGenerator() => VectorFiles.Rows(File.KeyGenerator, c => c.Salt);
    public static TheoryData<CookieEscapingCase> CookieEscaping() => VectorFiles.Rows(File.CookieEscaping);
    public static TheoryData<SignedCookieGenerate> SignedCookieGenerate() => VectorFiles.Rows(File.SignedCookies.Generate);
    public static TheoryData<SignedCookieVerify> SignedCookieVerify() => VectorFiles.Rows(File.SignedCookies.Verify, c => c.Case);
    public static TheoryData<EncryptedCookieGenerate> EncryptedCookieGenerate() => VectorFiles.Rows(File.EncryptedCookies.Generate);
    public static TheoryData<EncryptedCookieVerify> EncryptedCookieVerify() => VectorFiles.Rows(File.EncryptedCookies.Verify, c => c.Case);
    public static TheoryData<CsrfFormToken> CsrfFormTokens() => VectorFiles.Rows(File.Csrf.FormTokens, c => $"{c.Method} {c.Action}");
    public static TheoryData<CsrfValidityCase> CsrfValidity() => VectorFiles.Rows(File.Csrf.Validity, c => c.Case);
    public static TheoryData<CsrfOriginCase> CsrfOrigin() => VectorFiles.Rows(File.Csrf.Origin);
    public static TheoryData<SignedIdGenerate> SignedIdGenerate() => VectorFiles.Rows(File.SignedIds.Generate);
    public static TheoryData<SignedIdVerify> SignedIdVerify() => VectorFiles.Rows(File.SignedIds.Verify, c => c.Case);
    public static TheoryData<GlobalIdCase> GlobalIds() => VectorFiles.Rows(File.GlobalIds, c => c.Gid);
    public static TheoryData<SgidGenerate> SgidGenerate() => VectorFiles.Rows(File.Sgids.Generate);
    public static TheoryData<SgidVerify> SgidVerify() => VectorFiles.Rows(File.Sgids.Verify, c => c.Case);
    public static TheoryData<UnverifiedSgidCase> UnverifiedSgids() => VectorFiles.Rows(File.UnverifiedSgids, c => c.Case);
    public static TheoryData<TurboStreamNameGenerate> TurboStreamNameGenerate() => VectorFiles.Rows(File.TurboStreamNames.Generate, c => c.StreamName);
    public static TheoryData<TurboStreamNameVerify> TurboStreamNameVerify() => VectorFiles.Rows(File.TurboStreamNames.Verify, c => c.Case);
    public static TheoryData<AppVerifierGenerate> AppVerifierGenerate() => VectorFiles.Rows(File.AppVerifiers.Generate);
    public static TheoryData<AppVerifierVerify> AppVerifierVerify() => VectorFiles.Rows(File.AppVerifiers.Verify, c => c.Case);
    public static TheoryData<PasswordDigest> PasswordDigests() => VectorFiles.Rows(File.Passwords.Digests);
    public static TheoryData<PasswordCheck> PasswordChecks() => VectorFiles.Rows(File.Passwords.Checks);
}
