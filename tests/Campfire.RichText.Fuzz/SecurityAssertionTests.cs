namespace Campfire.RichText.Fuzz;

public class SecurityAssertionTests
{
    [Theory]
    [InlineData("<p onclick=\"x()\">a</p>")]
    [InlineData("<a href=\" java\tscript:alert(1)\">a</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">a</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">a</a>")]
    [InlineData("<img srcset=\"a.png 1x, javascript:x 2x\">")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    [InlineData("<math><mi>x</mi></math>")]
    [InlineData("<p style=\"color:red\">a</p>")]
    [InlineData("<iframe srcdoc=\"x\"></iframe>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">a</a>")]
    [InlineData("<form><button formaction=\"javascript:x\">b</button></form>")]
    public void Catch_planted_defects(string html)
    {
        Assert.NotEmpty(SecurityAssertions.Violations(html));
    }

    [Theory]
    [InlineData("<div class=\"lexxy-content\">\n  <p>Hello <a target=\"_blank\" href=\"https://example.com\">https://example.com</a></p>\n</div>\n")]
    [InlineData("<a href=\"mailto:me@example.com\">me@example.com</a> <a href=\"/rooms/1\">room</a>")]
    [InlineData("<img src=\"/users/1/avatar?v=1\" width=\"48\" height=\"48\"> <a href=\"https://example.com/javascript:x\">x</a>")]
    [InlineData("<blockquote href=\"javascript:x\">inert</blockquote>")]
    public void Pass_safe_markup(string html)
    {
        Assert.Empty(SecurityAssertions.Violations(html));
    }
}
