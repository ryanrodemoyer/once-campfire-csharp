using System.Security.Cryptography;

namespace Campfire.Server.Cli;

/// <summary>
/// <c>Rails.application.secret_key_base</c> (railties/lib/rails/application/configuration.rb at the
/// pinned revision): <c>SECRET_KEY_BASE_DUMMY</c> means a local secret, otherwise
/// <c>SECRET_KEY_BASE</c>, falling back to a local secret only in development and test. The
/// reference has no credentials file, so <c>credentials.secret_key_base</c> is always nil.
/// </summary>
public static class SecretKeyBase
{
    public static string Resolve(ServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SecretKeyBaseDummy)
        {
            return LocalSecret(settings);
        }

        return settings.SecretKeyBase switch
        {
            null when settings.RailsEnv is "development" or "test" => LocalSecret(settings),
            null => throw new SettingsException(
                $"Missing `secret_key_base` for '{settings.RailsEnv}' environment, set this string with `bin/rails credentials:edit`"),
            // secret_key_base= takes only a present string; a blank one falls through to its type check.
            var blank when string.IsNullOrWhiteSpace(blank) => throw new SettingsException(
                $"`secret_key_base` for {settings.RailsEnv} environment must be a type of String`"),
            var secret => secret,
        };
    }

    // generate_local_secret: SecureRandom.hex(64), kept in tmp/local_secret.txt.
    static string LocalSecret(ServerSettings settings)
    {
        var keyFile = settings.Resolve(Path.Combine("tmp", "local_secret.txt"));
        if (!File.Exists(keyFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
            File.WriteAllText(keyFile, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(64)));
        }

        return File.ReadAllText(keyFile);
    }
}
