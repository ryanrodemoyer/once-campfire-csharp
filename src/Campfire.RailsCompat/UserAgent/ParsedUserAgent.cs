namespace Campfire.RailsCompat.UserAgent;

/// <summary>One product of a User-Agent: <c>UserAgent.new(product, version, comment)</c>.</summary>
sealed record Product(string Name, UserAgentVersion Version, IReadOnlyList<string>? Comment)
{
    public string? CommentAt(int index) => Comment is not null && index < Comment.Count ? Comment[index] : null;

    public string? JoinedComment => Comment is null ? null : string.Join("; ", Comment);
}

/// <summary>The class from <c>UserAgent::Browsers</c> a parsed agent is extended with.</summary>
enum BrowserKind
{
    Base,
    Edge,
    InternetExplorer,
    Opera,
    WechatBrowser,
    Vivaldi,
    Chrome,
    ITunes,
    PlayStation,
    PodcastAddict,
    Webkit,
    Gecko,
    WindowsMediaPlayer,
    AppleCoreMedia,
    Libavformat,
}

/// <summary>
/// A port of the useragent gem (0.16.11), which Rails' <c>allow_browser</c> and platform_agent
/// use to read the User-Agent header. <see cref="Parse"/> splits the header into products, and
/// the first of <c>UserAgent::Browsers::ALL</c> whose <c>extend?</c> accepts them decides how
/// <c>browser</c>, <c>version</c>, <c>platform</c>, <c>os</c>, <c>bot?</c> and <c>mobile?</c>
/// are answered. Where the gem raises, these methods throw <see cref="GemRaisedException"/>.
/// </summary>
public sealed class ParsedUserAgent
{
    const string defaultUserAgent = "Mozilla/4.0 (compatible)";

    /// <summary><c>UserAgent::Browsers::ALL</c>, in detection order.</summary>
    static readonly BrowserKind[] DetectionOrder =
    [
        BrowserKind.Edge, BrowserKind.InternetExplorer, BrowserKind.Opera, BrowserKind.WechatBrowser,
        BrowserKind.Vivaldi, BrowserKind.Chrome, BrowserKind.ITunes, BrowserKind.PlayStation,
        BrowserKind.PodcastAddict, BrowserKind.Webkit, BrowserKind.Gecko, BrowserKind.WindowsMediaPlayer,
        BrowserKind.AppleCoreMedia, BrowserKind.Libavformat,
    ];

    readonly BrowserKind kind;
    readonly List<Product> products;

    ParsedUserAgent(BrowserKind kind, List<Product> products)
    {
        this.kind = kind;
        this.products = products;
    }

    /// <summary><c>UserAgent.parse</c>: a nil or blank string parses as "Mozilla/4.0 (compatible)".</summary>
    public static ParsedUserAgent Parse(string? userAgent)
    {
        var rest = userAgent is null || RubyText.Strip(userAgent).Length == 0 ? defaultUserAgent : userAgent;
        var products = new List<Product>();

        for (var match = GemPatterns.Product().Match(rest); match.Success; match = GemPatterns.Product().Match(rest))
        {
            var comment = match.Groups[4].Success ? RubyText.Split(match.Groups[4].Value, "; ") : null;
            products.Add(new Product(match.Groups[1].Value, new UserAgentVersion(match.Groups[2].Value), comment));
            rest = RubyText.Strip(rest[match.Length..]);
        }

        var kind = DetectionOrder.FirstOrDefault(k => Extends(k, products), BrowserKind.Base);
        return new ParsedUserAgent(kind, products);
    }

    /// <summary>Each browser class's <c>self.extend?(agent)</c>.</summary>
    static bool Extends(BrowserKind kind, List<Product> products)
    {
        var first = products.FirstOrDefault();
        var last = products.LastOrDefault();
        var firstVersion = first?.Version.Text;
        bool Any(string name) => products.Any(p => p.Name == name);

        return kind switch
        {
            BrowserKind.Edge => last?.Name == "Edge",
            BrowserKind.InternetExplorer => first?.Comment is not null &&
                ((first.CommentAt(1)?.Contains("MSIE", StringComparison.Ordinal) ?? false) ||
                 GemPatterns.TridentRv().IsMatch(first.JoinedComment!)),
            BrowserKind.Opera => first?.Name == "Opera" || last?.Name == "OPR",
            BrowserKind.WechatBrowser => products.Any(p => RubyText.ContainsIgnoringCase(p.Name, "micromessenger")),
            BrowserKind.Vivaldi => Any("Vivaldi"),
            BrowserKind.Chrome => Any("Chrome") || Any("CriOS"),
            BrowserKind.ITunes => Any("iTunes"),
            BrowserKind.PlayStation => first?.CommentAt(0) is { } c &&
                (c.Contains("PLAYSTATION 3", StringComparison.Ordinal) ||
                 c.Contains("PlayStation Vita", StringComparison.Ordinal) ||
                 c.Contains("PlayStation 4", StringComparison.Ordinal)),
            BrowserKind.PodcastAddict => products.Count >= 3 &&
                products[0].Name == "Podcast" && products[1].Name == "Addict" && products[2].Name == "-",
            BrowserKind.Webkit => products.Any(p =>
                RubyText.SameIgnoringCase(p.Name, "applewebkit") ||
                (p.Comment ?? []).Any(c => GemPatterns.WebkitComment().IsMatch(c))),
            BrowserKind.Gecko => first?.Name == "Mozilla",
            BrowserKind.WindowsMediaPlayer => products.Any(p =>
                p.Name is "NSPlayer" or "Windows-Media-Player" or "WMFSDK" &&
                firstVersion is not ("4.1.0.3856" or "7.10.0.3059" or "7.0.0.1956")),
            BrowserKind.AppleCoreMedia => Any("AppleCoreMedia"),
            BrowserKind.Libavformat => products.Any(p =>
                p.Name == "Lavf" || (p.Name == "NSPlayer" && firstVersion == "4.1.0.3856")),
            _ => true,
        };
    }

    // --- Base helpers ---

    Product? First => products.Count > 0 ? products[0] : null;

    Product? Last => products.Count > 0 ? products[^1] : null;

    IEnumerable<string> AllComments => products.SelectMany(p => p.Comment ?? []);

    /// <summary><c>detect_product</c>: a case-insensitive lookup by product name.</summary>
    Product? DetectProduct(string name) => products.Find(p => RubyText.SameIgnoringCase(p.Name, name));

    /// <summary>
    /// <c>application</c>: most classes use the first product; the WebKit-based ones the first
    /// product with a non-empty comment.
    /// </summary>
    Product? Application => kind is BrowserKind.Chrome or BrowserKind.Vivaldi or BrowserKind.Webkit or BrowserKind.ITunes or BrowserKind.AppleCoreMedia
        ? products.Find(p => p.Comment is { Count: > 0 })
        : First;

    IReadOnlyList<string>? ApplicationComment => Application?.Comment;

    UserAgentVersion? BaseVersion => Application?.Version;

    // --- bot? ---

    /// <summary><c>bot?</c>.</summary>
    public bool IsBot
    {
        get
        {
            if (Application is not { } application)
            {
                return true;
            }
            return AllComments.Any(c => RubyText.ContainsIgnoringCase(c, "bot")) ||
                DetectProduct("Chrome-Lighthouse") is not null ||
                application.Name.Contains("bot", StringComparison.Ordinal);
        }
    }

    // --- browser ---

    /// <summary><c>browser</c>; null for an unparseable string or a bare PlayStation Vita.</summary>
    public string? Browser() => kind switch
    {
        BrowserKind.Base => Application?.Name,
        BrowserKind.Edge => "Edge",
        BrowserKind.InternetExplorer => "Internet Explorer",
        BrowserKind.Opera => "Opera",
        BrowserKind.WechatBrowser => "Wechat Browser",
        BrowserKind.Vivaldi => "Vivaldi",
        BrowserKind.Chrome => DetectProduct("Iron") is not null ? "Iron" : "Chrome",
        BrowserKind.ITunes => "iTunes",
        BrowserKind.PlayStation => PlayStationBrowser(),
        BrowserKind.PodcastAddict => "Podcast Addict",
        BrowserKind.Webkit => WebkitBrowser(),
        BrowserKind.Gecko => GeckoBrowser(),
        BrowserKind.WindowsMediaPlayer => "Windows Media Player",
        BrowserKind.AppleCoreMedia => "AppleCoreMedia",
        _ => "libavformat",
    };

    string? PlayStationBrowser()
    {
        if (ApplicationComment is not [var firstComment, ..])
        {
            return null;
        }
        if (firstComment.Contains("PLAYSTATION 3", StringComparison.Ordinal))
        {
            return "PS3 Internet Browser";
        }
        if (Last?.Name == "Silk")
        {
            return "Silk";
        }
        return firstComment.Contains("PlayStation 4", StringComparison.Ordinal) ? "PS4 Internet Browser" : null;
    }

    string WebkitBrowser()
    {
        if (WebkitOs()?.Contains("Android", StringComparison.Ordinal) ?? false)
        {
            return "Android";
        }
        return WebkitPlatform() == "BlackBerry" ? "BlackBerry" : "Safari";
    }

    static readonly string[] GeckoBrowsers = ["PaleMoon", "Firefox", "Camino", "Iceweasel", "Seamonkey"];

    string GeckoBrowser() =>
        GeckoBrowsers.FirstOrDefault(name => DetectProduct(name) is not null)
        ?? First?.Name ?? "";

    // --- version ---

    /// <summary><c>version</c>; null where the gem answers nil.</summary>
    public UserAgentVersion? Version() => kind switch
    {
        BrowserKind.Base or BrowserKind.WindowsMediaPlayer or BrowserKind.AppleCoreMedia => BaseVersion,
        BrowserKind.Edge or BrowserKind.Vivaldi => Last?.Version,
        BrowserKind.InternetExplorer => new UserAgentVersion(
            GemPatterns.Capture(GemPatterns.IeVersion(), Application?.JoinedComment ?? "", 2) ?? ""),
        BrowserKind.Opera => OperaVersion(),
        BrowserKind.WechatBrowser => Required(DetectProduct("MicroMessenger")).Version,
        BrowserKind.Chrome => Required(DetectProduct("CriOs") ?? DetectProduct("chrome")).Version,
        BrowserKind.ITunes => Required(DetectProduct("iTunes")).Version,
        BrowserKind.PlayStation => PlayStationVersion(),
        BrowserKind.PodcastAddict => null,
        BrowserKind.Webkit => WebkitVersion(),
        BrowserKind.Gecko => GeckoVersion(),
        _ => DetectProduct("NSPlayer") is not null ? null : BaseVersion,
    };

    static T Required<T>(T? value) where T : class =>
        value ?? throw new GemRaisedException("undefined method for nil");

    bool IsOperaMini =>
        // `/Opera Mini/ === application` matches UserAgent#to_str; only the comment can hold a space.
        First?.JoinedComment?.Contains("Opera Mini", StringComparison.Ordinal) ?? false;

    UserAgentVersion? OperaVersion()
    {
        if (IsOperaMini)
        {
            // `rescue Version.new` covers a comment without an "Opera Mini/<version>".
            var comment = ApplicationComment?.FirstOrDefault(c => c.Contains("Opera Mini", StringComparison.Ordinal));
            return new UserAgentVersion(comment is null ? "" : GemPatterns.Capture(GemPatterns.OperaMiniVersion(), comment, 1) ?? "");
        }
        return (DetectProduct("Version") ?? DetectProduct("OPR"))?.Version ?? BaseVersion;
    }

    UserAgentVersion? PlayStationVersion()
    {
        if (PlayStationOs() is not { } os)
        {
            return null;
        }
        if (PlayStationBrowser() == "Silk")
        {
            return Last?.Version;
        }

        UserAgentVersion After(string marker) => new(RubyText.Split(os, marker) is [.., var tail] ? tail : "");
        return PlayStationPlatform() switch
        {
            "PlayStation 3" => After("PLAYSTATION 3 "),
            "PlayStation 4" => After("PlayStation 4 "),
            "PlayStation Vita" => After("PlayStation Vita "),
            _ => null,
        };
    }

    UserAgentVersion WebkitVersion()
    {
        if (DetectProduct("Version") is { } version)
        {
            return version.Version;
        }
        if (WebkitOs() is { } os && GemPatterns.Capture(GemPatterns.IosOsVersion(), os, 1) is { } ios && WebkitBrowser() == "Safari")
        {
            return new UserAgentVersion(ios.Replace('_', '.'));
        }
        return new UserAgentVersion(WebkitBuildVersion(Webkit()?.Text ?? "") ?? "");
    }

    /// <summary><c>Webkit#webkit.version</c>: the AppleWebKit product's version, or one from a comment.</summary>
    UserAgentVersion? Webkit()
    {
        if (products.Find(p => RubyText.SameIgnoringCase(p.Name, "applewebkit")) is { } product)
        {
            return product.Version;
        }
        var fromComment = AllComments.Select(c => GemPatterns.WebkitComment().Match(c)).FirstOrDefault(m => m.Success);
        return fromComment is null ? null : new UserAgentVersion(fromComment.Groups["version"].Value);
    }

    UserAgentVersion? GeckoVersion()
    {
        var version = Required(DetectProduct(GeckoBrowser())).Version;
        return version.IsNil ? BaseVersion : version;
    }

    // --- platform ---

    /// <summary><c>platform</c>.</summary>
    public string? Platform()
    {
        var comment = ApplicationComment;
        var first = comment is [var head, ..] ? head : null;
        bool FirstHas(string needle) => first?.Contains(needle, StringComparison.Ordinal) ?? false;
        bool AnyHas(string needle) => comment?.Any(c => c.Contains(needle, StringComparison.Ordinal)) ?? false;

        switch (kind)
        {
            case BrowserKind.Base or BrowserKind.Libavformat:
                return null;
            case BrowserKind.Edge or BrowserKind.InternetExplorer or BrowserKind.WindowsMediaPlayer:
                return "Windows";
            case BrowserKind.Webkit or BrowserKind.ITunes:
                return WebkitPlatform();
            case BrowserKind.PlayStation:
                return PlayStationPlatform();
            case BrowserKind.PodcastAddict:
                return Required(PodcastAddictOs()).Contains("Android", StringComparison.Ordinal) ? "Android" : null;
        }

        if (comment is null)
        {
            return null;
        }
        return kind switch
        {
            BrowserKind.Opera or BrowserKind.AppleCoreMedia => FirstHas("Windows") ? "Windows" : first,
            BrowserKind.WechatBrowser => FirstHas("iPhone") ? "iPhone" : AnyHas("Android") ? "Android" : first,
            BrowserKind.Chrome or BrowserKind.Vivaldi =>
                FirstHas("Windows") ? "Windows" : AnyHas("CrOS") ? "ChromeOS" : AnyHas("Android") ? "Android" : first,
            _ => first switch // Gecko
            {
                "compatible" or "Mobile" => null,
                { } c when c.StartsWith("Windows ", StringComparison.Ordinal) => "Windows",
                var other => other,
            },
        };
    }

    string? WebkitPlatform()
    {
        if (ApplicationComment is not { } comment)
        {
            return null;
        }
        var first = comment.Count > 0 ? comment[0] : null;
        if (first?.Contains("Windows", StringComparison.Ordinal) ?? false)
        {
            return "Windows";
        }
        if (first == "BB10")
        {
            return "BlackBerry";
        }
        return comment.Any(c => c.Contains("Android", StringComparison.Ordinal)) ? "Android" : first;
    }

    string? PlayStationPlatform()
    {
        var os = PlayStationOs();
        if (os is null)
        {
            return null;
        }
        return os.Contains("PLAYSTATION 3", StringComparison.Ordinal) ? "PlayStation 3"
            : os.Contains("PlayStation 4", StringComparison.Ordinal) ? "PlayStation 4"
            : os.Contains("PlayStation Vita", StringComparison.Ordinal) ? "PlayStation Vita"
            : null;
    }

    // --- os ---

    /// <summary><c>os</c>.</summary>
    public string? Os() => kind switch
    {
        BrowserKind.Base or BrowserKind.Libavformat => null,
        BrowserKind.Edge => OperatingSystems.Normalize(
            AllComments.Select(c => GemPatterns.WindowsOs().Match(c)).FirstOrDefault(m => m.Success)?.Value ?? ""),
        BrowserKind.InternetExplorer => OperatingSystems.Normalize(
            GemPatterns.WindowsOs().Match(Application?.JoinedComment ?? "") is { Success: true } m ? m.Value : ""),
        BrowserKind.Opera => OperaOs(),
        BrowserKind.WechatBrowser or BrowserKind.Chrome or BrowserKind.Vivaldi or BrowserKind.AppleCoreMedia =>
            ApplicationComment is { } comment ? ChromeOs(comment) : null,
        BrowserKind.Webkit => WebkitOs(),
        BrowserKind.ITunes => ITunesOs(),
        BrowserKind.PlayStation => PlayStationOs(),
        BrowserKind.PodcastAddict => PodcastAddictOs(),
        BrowserKind.Gecko => GeckoOs(),
        _ => WindowsMediaPlayerOs(),
    };

    string? OperaOs()
    {
        if (ApplicationComment is not { } comment)
        {
            return null;
        }
        if (comment.Count > 0 && comment[0].Contains("Windows", StringComparison.Ordinal))
        {
            return OperatingSystems.Normalize(comment[0]);
        }
        return comment.Count > 1 ? comment[1] : null;
    }

    /// <summary><c>os</c> shared by Chrome, Vivaldi, WechatBrowser and AppleCoreMedia.</summary>
    static string? ChromeOs(IReadOnlyList<string> comment)
    {
        string? At(int i) => i < comment.Count ? comment[i] : null;
        var pick = (At(0)?.Contains("Windows NT", StringComparison.Ordinal) ?? false) ? At(0)
            : At(2) is null || (At(1)?.Contains("Android", StringComparison.Ordinal) ?? false) ? At(1)
            : At(2);
        return pick is null ? null : OperatingSystems.Normalize(pick);
    }

    string? WebkitOs()
    {
        if (ApplicationComment is not { } comment)
        {
            return null;
        }
        string? At(int i) => i < comment.Count ? comment[i] : null;

        if (At(0)?.Contains("Windows NT", StringComparison.Ordinal) ?? false)
        {
            return OperatingSystems.Normalize(At(0)!);
        }
        if (At(2) is null || (At(1)?.Contains("Android", StringComparison.Ordinal) ?? false))
        {
            return At(1) is { } second ? OperatingSystems.Normalize(second) : null;
        }
        var ios = comment.FirstOrDefault(c => GemPatterns.IosVersion().IsMatch(c));
        return OperatingSystems.Normalize(ios ?? At(2)!);
    }

    string? ITunesOs()
    {
        var windows = ApplicationComment is [var first, ..] && first.Contains("Windows", StringComparison.Ordinal);
        if (!windows)
        {
            return WebkitOs();
        }

        var fullOs = ITunesFullOs() ?? "";
        string[] names = ["Windows 8.1", "Windows 8", "Windows 7", "Windows Vista", "Windows XP"];
        return names.FirstOrDefault(name => fullOs.Contains(name, StringComparison.Ordinal)) ?? "Windows";
    }

    /// <summary><c>ITunes#full_os</c>: the comment was cut at the first ")", so "(Build 7601" gets it back.</summary>
    string? ITunesFullOs()
    {
        if (ApplicationComment is not { Count: > 1 } comment)
        {
            return null;
        }
        var fullOs = comment[1];
        return GemPatterns.CutBuild().IsMatch(fullOs) ? fullOs + ")" : fullOs;
    }

    string? PlayStationOs() => ApplicationComment is { } comment ? string.Join(" ", comment) : null;

    /// <summary><c>PodcastAddict#os</c>; the gem raises on a comment-less Dalvik or Mozilla.</summary>
    string? PodcastAddictOs()
    {
        if (products.Count <= 3 || products[3].Name is not ("Dalvik" or "Mozilla"))
        {
            return null;
        }
        var comment = Required(products[3].Comment);
        return comment.Count switch
        {
            > 3 => comment[2],
            3 => "Android",
            _ => null,
        };
    }

    string? GeckoOs()
    {
        if (ApplicationComment is not { } comment)
        {
            return null;
        }
        var first = comment.Count > 0 ? comment[0] : null;
        int index;
        if (comment.Count > 1 && comment[1] == "U")
        {
            index = 2;
        }
        else if (first is not null && (first.StartsWith("Windows ", StringComparison.Ordinal) || first.StartsWith("Android", StringComparison.Ordinal)))
        {
            index = 0;
        }
        else if (first == "Mobile")
        {
            return null;
        }
        else
        {
            index = 1;
        }
        return index < comment.Count ? OperatingSystems.Normalize(comment[index]) : null;
    }

    /// <summary>
    /// <c>version.to_a[0]</c> compared with an Integer: nil raises NoMethodError, a String
    /// ArgumentError.
    /// </summary>
    long WindowsMediaPlayerMajor()
    {
        var version = Required(BaseVersion);
        return version.ToA() is [{ IsInteger: true } major, ..]
            ? major.AsInt64() ?? long.MaxValue
            : throw new GemRaisedException("comparison of String with Integer failed");
    }

    string WindowsMediaPlayerOs()
    {
        var major = WindowsMediaPlayerMajor();
        var segments = (BaseVersion ?? UserAgentVersion.Empty).ToA();
        long? Part(int i) => i < segments.Count ? segments[i].AsInt64() : null;

        return major switch
        {
            <= 4 => Part(3) switch
            {
                3564 or 3925 => "Windows 98",
                3857 => "Windows 9x",
                3936 => "Windows XP",
                3938 => "Windows 2000",
                _ => "Windows",
            },
            7 => Part(3) == 3055 ? "Windows 98" : "Windows",
            8 => "Windows XP",
            9 or 10 => Part(3) switch
            {
                2980 => "Windows 98/2000",
                3268 or 3367 or 3270 => "Windows 2000",
                3802 or 4503 => "Windows XP",
                _ => "Windows",
            },
            11 or 12 => Part(2) switch
            {
                9841 or 9858 or 9860 or 9879 => "Windows 10",
                9651 => "Windows Phone 8.1",
                9600 => "Windows 8.1",
                9200 => "Windows 8",
                7600 or 7601 => "Windows 7",
                >= 6000 and <= 6002 => "Windows Vista",
                5721 => "Windows XP",
                _ => "Windows",
            },
            _ => "Windows",
        };
    }

    // --- mobile? ---

    /// <summary><c>mobile?</c>.</summary>
    public bool IsMobile() => kind switch
    {
        BrowserKind.Opera => IsOperaMini,
        BrowserKind.PlayStation => PlayStationPlatform() == "PlayStation Vita",
        BrowserKind.PodcastAddict => true,
        BrowserKind.WindowsMediaPlayer => WindowsMediaPlayerOs() is "Windows Phone 8" or "Windows Phone 8.1",
        _ => DetectProduct("Mobile") is not null ||
            AllComments.Any(c => c == "Mobile") ||
            (Os()?.Contains("Android", StringComparison.Ordinal) ?? false) ||
            (ApplicationComment?.Any(c => c.StartsWith("IEMobile", StringComparison.Ordinal)) ?? false),
    };

    /// <summary><c>Webkit::BuildVersions</c>: Safari versions before Safari 3 reported only the WebKit build.</summary>
    static string? WebkitBuildVersion(string build) => build switch
    {
        "85.7" => "1.0",
        "85.8.5" or "85.8.2" => "1.0.3",
        "124" => "1.2",
        "125.2" => "1.2.2",
        "125.4" => "1.2.3",
        "125.5.5" or "125.5.6" or "125.5.7" => "1.2.4",
        "312.1.1" or "312.1" => "1.3",
        "312.5" or "312.5.1" or "312.5.2" => "1.3.1",
        "312.8" or "312.8.1" => "1.3.2",
        "412" or "412.6" or "412.6.2" => "2.0",
        "412.7" => "2.0.1",
        "416.11" or "416.12" => "2.0.2",
        "417.9" or "418" => "2.0.3",
        "418.8" or "418.9" or "418.9.1" or "419" => "2.0.4",
        "425.13" => "2.2",
        "534.52.7" => "5.1.2",
        _ => null,
    };
}
