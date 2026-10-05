namespace Campfire.Web.Assets;

// The parts of Ruby's File and Pathname that Propshaft and importmap-rails lean on, for the
// relative, "/"-separated logical paths they work with.
static class RubyPath
{
    // File.basename: trailing slashes are ignored, and a path of only slashes is "/".
    public static string Basename(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return path.Length == 0 ? "" : "/";
        }

        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    // File.dirname
    public static string Dirname(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return path.Length == 0 ? "." : "/";
        }

        var slash = trimmed.LastIndexOf('/');
        if (slash < 0)
        {
            return ".";
        }

        var directory = trimmed[..slash].TrimEnd('/');
        return directory.Length == 0 ? "/" : directory;
    }

    // File.extname: leading dots of the basename don't start an extension.
    public static string Extname(string path)
    {
        var name = Basename(path).TrimStart('.');
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }

    // File.join for two parts: exactly one "/" between them.
    public static string Join(string left, string right)
    {
        if (left.EndsWith('/') && right.StartsWith('/'))
        {
            return left + right[1..];
        }

        return left.EndsWith('/') || right.StartsWith('/') ? left + right : left + "/" + right;
    }

    // Pathname#+ (Pathname#plus): leading "." and ".." of the right side are resolved against the
    // left side; the rest of the right side is kept exactly as written.
    public static string Plus(string path1, string path2)
    {
        var prefix2 = path2;
        var indexList2 = new List<int>();
        var basenameList2 = new List<string>();
        while (ChopBasename(prefix2) is var (chopped2, basename2))
        {
            prefix2 = chopped2;
            indexList2.Insert(0, prefix2.Length);
            basenameList2.Insert(0, basename2);
        }

        if (prefix2.Length != 0)
        {
            return path2;
        }

        var prefix1 = path1;
        while (true)
        {
            while (basenameList2.Count > 0 && basenameList2[0] == ".")
            {
                indexList2.RemoveAt(0);
                basenameList2.RemoveAt(0);
            }

            if (ChopBasename(prefix1) is not var (chopped1, basename1))
            {
                break;
            }

            prefix1 = chopped1;
            if (basename1 == ".")
            {
                continue;
            }

            if (basename1 == ".." || basenameList2.Count == 0 || basenameList2[0] != "..")
            {
                prefix1 += basename1;
                break;
            }

            indexList2.RemoveAt(0);
            basenameList2.RemoveAt(0);
        }

        var hasBasename = ChopBasename(prefix1) is not null;
        if (!hasBasename && Basename(prefix1).Contains('/', StringComparison.Ordinal))
        {
            while (basenameList2.Count > 0 && basenameList2[0] == "..")
            {
                indexList2.RemoveAt(0);
                basenameList2.RemoveAt(0);
            }
        }

        if (basenameList2.Count > 0)
        {
            var suffix2 = path2[indexList2[0]..];
            return hasBasename ? Join(prefix1, suffix2) : prefix1 + suffix2;
        }

        return hasBasename ? prefix1 : Dirname(prefix1);
    }

    // Pathname#cleanpath (the aggressive one) for a relative path; leading ".." are kept.
    public static string Cleanpath(string path)
    {
        var names = new List<string>();
        foreach (var name in path.Split('/'))
        {
            if (name is "" or ".")
            {
                continue;
            }

            if (name == ".." && names.Count > 0 && names[^1] != "..")
            {
                names.RemoveAt(names.Count - 1);
            }
            else
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? "." : string.Join('/', names);
    }

    // Pathname#chop_basename: (everything before the basename, the basename), or null at the root.
    static (string Prefix, string Basename)? ChopBasename(string path)
    {
        var basename = Basename(path);
        if (basename is "" or "/")
        {
            return null;
        }

        return (path[..path.LastIndexOf(basename, StringComparison.Ordinal)], basename);
    }
}
