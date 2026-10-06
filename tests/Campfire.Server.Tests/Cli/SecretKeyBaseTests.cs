using Campfire.Server.Cli;

namespace Campfire.Server.Tests.Cli;

public sealed class SecretKeyBaseTests : IDisposable
{
    readonly TestRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void Production_takes_secret_key_base()
    {
        Assert.Equal("from-env", SecretKeyBase.Resolve(root.Settings(("SECRET_KEY_BASE", "from-env"))));
    }

    [Fact]
    public void Production_refuses_to_start_without_one()
    {
        var error = Assert.Throws<SettingsException>(() => SecretKeyBase.Resolve(root.Settings()));
        Assert.Equal("Missing `secret_key_base` for 'production' environment, set this string with `bin/rails credentials:edit`", error.Message);
    }

    [Fact]
    public void A_blank_secret_fails_secret_key_bases_type_check()
    {
        var error = Assert.Throws<SettingsException>(() => SecretKeyBase.Resolve(root.Settings(("SECRET_KEY_BASE", ""))));
        Assert.Equal("`secret_key_base` for production environment must be a type of String`", error.Message);
    }

    [Theory]
    [InlineData("production", "SECRET_KEY_BASE_DUMMY", "")]
    [InlineData("development", null, null)]
    [InlineData("test", null, null)]
    public void Otherwise_a_local_secret_is_kept_in_tmp(string env, string? name, string? value)
    {
        var settings = root.Settings(("RAILS_ENV", env), (name ?? "UNUSED", value));

        var secret = SecretKeyBase.Resolve(settings);

        Assert.Matches("^[0-9a-f]{128}$", secret);
        Assert.Equal(secret, File.ReadAllText(System.IO.Path.Combine(root.Path, "tmp", "local_secret.txt")));
        Assert.Equal(secret, SecretKeyBase.Resolve(settings));
    }

    [Fact]
    public void The_dummy_wins_over_a_real_secret()
    {
        Assert.NotEqual("real", SecretKeyBase.Resolve(root.Settings(("SECRET_KEY_BASE", "real"), ("SECRET_KEY_BASE_DUMMY", "1"))));
    }
}
