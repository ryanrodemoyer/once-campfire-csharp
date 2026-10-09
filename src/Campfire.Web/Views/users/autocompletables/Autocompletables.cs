namespace Campfire.Web.Helpers;

// reference/app/views/users/autocompletables/_template.html.erb
// dotnet format analyzes without the template generator, so it sees these parameters unused.
#pragma warning disable IDE0060
public partial class View
{
    /// <summary>
    /// <c>users/autocompletables/_template</c>: the pill an autocomplete fills in for each picked user.
    /// </summary>
    [ErbTemplate("users/autocompletables/_template.html.erb.cs")]
    public partial void UsersAutocompletablesTemplate(HtmlWriter w);
}
#pragma warning restore IDE0060
