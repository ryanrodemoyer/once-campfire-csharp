using System.Globalization;

namespace Campfire.Server.Cli;

/// <summary>
/// The environment the reference image reads (<c>reference/Dockerfile</c>, <c>reference/config/</c>),
/// plus where the port finds what Rails keeps under <c>Rails.root</c>. Relative paths are relative to
/// <see cref="Root"/>, the working directory (<c>/rails</c> in the image), as Rails resolves them
/// against <c>Rails.root</c>.
/// </summary>
public sealed record ServerSettings
{
    /// <summary><c>Rails.root</c>.</summary>
    public required string Root { get; init; }

    /// <summary><c>RAILS_ENV</c>. The port only has the production configuration, so that's the default.</summary>
    public string RailsEnv { get; init; } = "production";

    /// <summary><c>SECRET_KEY_BASE</c>, null when unset. See <see cref="SecretKeyBase"/>.</summary>
    public string? SecretKeyBase { get; init; }

    /// <summary><c>SECRET_KEY_BASE_DUMMY</c> is set (to anything, even empty).</summary>
    public bool SecretKeyBaseDummy { get; init; }

    /// <summary><c>VAPID_PUBLIC_KEY</c> and <c>VAPID_PRIVATE_KEY</c> (reference/config/initializers/vapid.rb).</summary>
    public string? VapidPublicKey { get; init; }

    public string? VapidPrivateKey { get; init; }

    /// <summary>
    /// <c>config.assume_ssl</c> and <c>config.force_ssl</c>, both <c>ENV["DISABLE_SSL"].blank?</c>
    /// (reference/config/environments/production.rb).
    /// </summary>
    public bool Ssl { get; init; } = true;

    /// <summary><c>TLS_DOMAIN</c>: Thruster's, read by the public front server (P03).</summary>
    public string? TlsDomain { get; init; }

    /// <summary><c>CAMPFIRE_STORAGE_PATH</c>, <c>storage</c> by default: <c>Rails.root.join("storage")</c>.</summary>
    public string StoragePath { get; init; } = "storage";

    /// <summary><c>CAMPFIRE_DATABASE_PATH</c>; by default <c>storage/db/&lt;env&gt;.sqlite3</c> (reference/config/database.yml).</summary>
    public string? DatabasePathOverride { get; init; }

    /// <summary>
    /// <c>CAMPFIRE_ASSETS_PATH</c>: the output of <c>bin/build-assets</c>, which stands in for
    /// <c>public/</c> after <c>assets:precompile</c>.
    /// </summary>
    public string AssetsPath { get; init; } = "artifacts/assets";

    /// <summary><c>PORT</c>, Puma's listener (reference/config/puma.rb).</summary>
    public int Port { get; init; } = 3000;

    /// <summary><c>RAILS_MAX_THREADS</c>: the database pool (reference/config/database.yml).</summary>
    public int MaxThreads { get; init; } = 10;

    /// <summary><c>APP_VERSION.presence || GIT_REVISION.presence || "0"</c> (reference/config/initializers/version.rb).</summary>
    public string AppVersion { get; init; } = "0";

    public string? GitRevision { get; init; }

    public string DatabasePath => Resolve(DatabasePathOverride ?? Path.Combine(StoragePath, "db", $"{RailsEnv}.sqlite3"));

    /// <summary>Active Storage's <c>local</c> service root (reference/config/storage.yml).</summary>
    public string FilesPath => Resolve(Path.Combine(StoragePath, "files"));

    /// <summary>Where <c>campfire backup</c> writes and the post-restore hook reads (reference/hooks).</summary>
    public string BackupsPath => Resolve(Path.Combine(StoragePath, "backups"));

    public string AssetsDirectory => Resolve(AssetsPath);

    public string Resolve(string path) => Path.GetFullPath(path, Root);

    public static ServerSettings FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Directory.GetCurrentDirectory());

    public static ServerSettings FromEnvironment(Func<string, string?> env, string root)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? Present(string name) => string.IsNullOrWhiteSpace(env(name)) ? null : env(name);

        var defaults = new ServerSettings { Root = root };
        return defaults with
        {
            RailsEnv = Present("RAILS_ENV") ?? defaults.RailsEnv,
            SecretKeyBase = env("SECRET_KEY_BASE"),
            SecretKeyBaseDummy = env("SECRET_KEY_BASE_DUMMY") is not null,
            VapidPublicKey = env("VAPID_PUBLIC_KEY"),
            VapidPrivateKey = env("VAPID_PRIVATE_KEY"),
            Ssl = Present("DISABLE_SSL") is null,
            TlsDomain = Present("TLS_DOMAIN"),
            StoragePath = Present("CAMPFIRE_STORAGE_PATH") ?? defaults.StoragePath,
            DatabasePathOverride = Present("CAMPFIRE_DATABASE_PATH"),
            AssetsPath = Present("CAMPFIRE_ASSETS_PATH") ?? defaults.AssetsPath,
            Port = Integer("PORT", Present("PORT")) ?? defaults.Port,
            MaxThreads = Integer("RAILS_MAX_THREADS", Present("RAILS_MAX_THREADS")) ?? defaults.MaxThreads,
            AppVersion = Present("APP_VERSION") ?? Present("GIT_REVISION") ?? defaults.AppVersion,
            GitRevision = env("GIT_REVISION"),
        };
    }

    static int? Integer(string name, string? value) =>
        value is null ? null
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number
        : throw new SettingsException($"{name} must be a positive integer, not \"{value}\"");
}

/// <summary>A setting is missing or malformed; the command prints the message and exits 1.</summary>
public sealed class SettingsException(string message) : Exception(message);
