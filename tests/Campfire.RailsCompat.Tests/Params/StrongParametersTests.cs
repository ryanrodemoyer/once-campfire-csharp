using Campfire.RailsCompat.Params;

namespace Campfire.RailsCompat.Tests.Params;

/// <summary>The <c>require</c>/<c>permit</c>/<c>fetch</c> calls in <c>reference/app/controllers</c>.</summary>
public class StrongParametersTests
{
    static ParamHash Query(string query) => ParamBuilder.FromQueryString(query);

    [Fact]
    public void RequireThenPermitKeepsOnlyPermittedScalars()
    {
        // users_controller.rb: params.require(:user).permit(:name, :avatar, :email_address, :password)
        var parameters = Query("user[name]=Jo&user[admin]=1&user[email_address][]=x&user[password]=secret&user[born_on(1i)]=2024");
        var permitted = parameters.RequireHash("user").Permit("name", "avatar", "email_address", "password", "born_on");
        Assert.Equal("""{"name":"Jo","password":"secret","born_on(1i)":"2024"}""", permitted.ToString());
    }

    [Fact]
    public void PermittedKeysFollowTheFilterOrder()
    {
        var permitted = Query("b=2&a=1").Permit("a", "b");
        Assert.Equal("""{"a":"1","b":"2"}""", permitted.ToString());
    }

    [Fact]
    public void RequireRaisesWhenMissingOrBlank()
    {
        var parameters = Query("blank=+&empty[]&url=https://x");
        Assert.Equal("blank", Assert.Throws<ParameterMissingException>(() => parameters.Require("blank")).Key);
        Assert.Throws<ParameterMissingException>(() => parameters.Require("empty"));
        Assert.Throws<ParameterMissingException>(() => parameters.Require("missing"));
        // unfurl_links_controller.rb: params.require(:url)
        Assert.Equal("https://x", parameters.Require("url"));
    }

    [Fact]
    public void RequireAcceptsFalse()
    {
        var parameters = ParamBuilder.FromJson("""{"flag":false}"""u8);
        Assert.Equal(false, parameters.Require("flag"));
    }

    [Fact]
    public void AnyHashKeepsEveryNestedValue()
    {
        // accounts_controller.rb: params.require(:account).permit(:name, :logo, settings: {})
        var parameters = ParamBuilder.FromJson("""{"account":{"name":"C","settings":{"a":"1","b":{"c":[1,[2],{"d":null}]}},"other":"x"}}"""u8);
        var permitted = parameters.RequireHash("account").Permit("name", "logo", PermitFilter.AnyHash("settings"));
        Assert.Equal("""{"name":"C","settings":{"a":"1","b":{"c":[1,[2],{"d":null}]}}}""", permitted.ToString());
        Assert.Equal("{}", Query("account[settings]=x").RequireHash("account").Permit(PermitFilter.AnyHash("settings")).ToString());
    }

    [Fact]
    public void ScalarArrays()
    {
        var parameters = Query("ids[]=1&ids[]=2&bad[][x]=1");
        Assert.Equal("""{"ids":["1","2"]}""", parameters.Permit(PermitFilter.ScalarArray("ids"), PermitFilter.ScalarArray("bad")).ToString());
    }

    [Fact]
    public void NestedFilters()
    {
        var parameters = Query("a[b][c]=1&a[b][d]=2&list[][c]=1&list[][d]=2&ff[0][c]=1&ff[1][c]=2&ff[x]=3&scalar=1");
        var c = PermitFilter.Key("c");
        var permitted = parameters.Permit(
            PermitFilter.Nested("a", PermitFilter.Nested("b", c)),
            PermitFilter.Nested("list", c),
            PermitFilter.Nested("ff", c),
            PermitFilter.Nested("scalar", c));
        Assert.Equal("""{"a":{"b":{"c":"1"}},"list":[{"c":"1"}],"ff":{"0":{"c":"1"},"1":{"c":"2"}}}""", permitted.ToString());
    }

    [Fact]
    public void FilesArePermittedScalars()
    {
        using var file = UploadedFile.FromBytes("me.png", "image/png", "x"u8);
        var parameters = ParamBuilder.FromPairs([ParamPair.File("user[avatar]", file)]);
        Assert.Same(file, parameters.RequireHash("user").Permit("avatar").GetFile("avatar"));
    }

    [Fact]
    public void FetchWithADefault()
    {
        // rooms/closeds_controller.rb: params.fetch(:user_ids, [])
        var parameters = Query("user_ids[]=1&user_ids[]=2&nil");
        Assert.Equal("""["1","2"]""", ParamValues.ToJson(parameters.Fetch("user_ids", new List<object?>()))!.ToJsonString());
        Assert.Equal(new List<object?>(), parameters.Fetch("nope", new List<object?>()));
        Assert.Null(parameters.Fetch("nil", "default"));
        Assert.Throws<ParameterMissingException>(() => parameters.Fetch("nope"));
    }
}
