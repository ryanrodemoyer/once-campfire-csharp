using System.Text.Json.Nodes;
using Campfire.Data.Queries;
using Campfire.Data.Records;

namespace Campfire.Data.Tests.Queries;

// reference/test/models/account_test.rb ("settings") and account/joinable_test.rb. The logo
// tests are the S lane's. The stored JSON in each case is what the reference app wrote, run with
// `bin/rails runner` on the parity seed.
public sealed class AccountTests : IDisposable
{
    static readonly DateTimeOffset Now = Fixtures.LoadedAt;

    readonly Fixtures.Database fixtures = new();

    public void Dispose() => fixtures.Dispose();

    Account Signal() => fixtures.Write(session => Accounts.Find(session, Fixtures.Id("signal"))!);

    static KeyValuePair<string, JsonNode?>[] Restrict(JsonNode? value) => [new(AccountSettings.RestrictRoomCreationToAdministratorsKey, value)];

    [Fact]
    public void Settings()
    {
        var settings = Signal().SettingsData.Assign(Restrict(true));
        Assert.True(settings.RestrictRoomCreationToAdministrators);
        Assert.Equal("""{"restrict_room_creation_to_administrators":true}""", settings.ToJson());

        var signal = Signal();
        var account = fixtures.Write(session => Accounts.Update(session, signal, Now, settings: signal.SettingsData.Assign(Restrict("true"))));
        Assert.True(Signal().SettingsData.RestrictRoomCreationToAdministrators);
        Assert.Equal("""{"restrict_room_creation_to_administrators":true}""", account.Settings);

        settings = account.SettingsData.Assign(Restrict(false));
        Assert.False(settings.RestrictRoomCreationToAdministrators);
        Assert.Equal("""{"restrict_room_creation_to_administrators":false}""", settings.ToJson());

        signal = Signal();
        fixtures.Write(session => Accounts.Update(session, signal, Now, settings: signal.SettingsData.Assign(Restrict("false"))));
        Assert.False(Signal().SettingsData.RestrictRoomCreationToAdministrators);
    }

    [Theory]
    [InlineData("", """{"restrict_room_creation_to_administrators":null}""")]
    [InlineData("off", """{"restrict_room_creation_to_administrators":false}""")]
    [InlineData("0", """{"restrict_room_creation_to_administrators":false}""")]
    [InlineData("yes", """{"restrict_room_creation_to_administrators":true}""")]
    public void Assigned_settings_are_cast_to_booleans(string value, string json)
    {
        Assert.Equal(json, AccountSettings.Parse(null).Assign(Restrict(value)).ToJson());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    [InlineData("null", false)]
    [InlineData("""{"restrict_room_creation_to_administrators":"false"}""", true)]
    [InlineData("""{"restrict_room_creation_to_administrators":" "}""", false)]
    [InlineData("""{"restrict_room_creation_to_administrators":0}""", true)]
    public void Stored_settings_are_present_or_not_as_rails_reads_them(string? json, bool restricted)
    {
        Assert.Equal(restricted, AccountSettings.Parse(json).RestrictRoomCreationToAdministrators);
    }

    [Fact]
    public void Settings_that_are_not_an_object_raise_as_in_rails()
    {
        Assert.Throws<InvalidDataException>(() => AccountSettings.Parse("[1]"));
    }

    [Fact]
    public void Unknown_settings_raise_as_in_rails()
    {
        Assert.Throws<ArgumentException>(() => AccountSettings.Parse(null).Assign([new("bogus", "1")]));
    }

    [Fact]
    public void Saving_writes_the_settings_defaults()
    {
        var created = fixtures.Write(session =>
        {
            session.Execute("DELETE FROM accounts");
            return Accounts.Create(session, "Chat", Now);
        });
        Assert.Equal("""{"restrict_room_creation_to_administrators":false}""", created.Settings);

        // Stored keys outside the schema stay, after the defaults.
        fixtures.Write(session => session.Execute("UPDATE accounts SET settings = '{\"other\": 1}'"));
        var saved = fixtures.Write(session => Accounts.Update(session, Accounts.First(session)!, Now + TimeSpan.FromMinutes(1), name: "Chat"));
        Assert.Equal("""{"restrict_room_creation_to_administrators":false,"other":1}""", saved.Settings);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), saved.UpdatedAt);

        // The same hash spelled differently isn't a change, so nothing is written.
        fixtures.Write(session => session.Execute("UPDATE accounts SET settings = '{ \"restrict_room_creation_to_administrators\" : false }'"));
        var unchanged = fixtures.Write(session => Accounts.First(session)!);
        Assert.Same(unchanged, fixtures.Write(session => Accounts.Update(session, unchanged, Now + TimeSpan.FromMinutes(2), name: "Chat")));
    }

    [Fact]
    public void Saving_an_account_with_null_settings_writes_the_defaults()
    {
        var signal = Signal();
        Assert.Null(signal.Settings);
        var saved = fixtures.Write(session => Accounts.Update(session, signal, Now + TimeSpan.FromMinutes(1)));
        Assert.Equal("""{"restrict_room_creation_to_administrators":false}""", saved.Settings);
    }

    [Fact]
    public void New_accounts_get_a_joinable_code()
    {
        var account = fixtures.Write(session =>
        {
            session.Execute("DELETE FROM accounts");
            return Accounts.Create(session, "Chat", Now);
        });
        Assert.Matches(@"^\w{4}-\w{4}-\w{4}$", account.JoinCode);
        Assert.Equal(0, account.SingletonGuard);
    }

    [Fact]
    public void Accounts_can_reset_join_code()
    {
        var signal = Signal();
        var reset = fixtures.Write(session => Accounts.ResetJoinCode(session, signal, Now + TimeSpan.FromMinutes(1)));
        Assert.NotEqual(signal.JoinCode, Signal().JoinCode);
        Assert.Equal(reset, Signal());
    }
}
