using System.Text;

namespace Campfire.Web.Assets;

// One module of the import map: its name and the asset path it resolves to.
sealed record ImportmapPin(string Name, string Path, bool Preload);

// A port of importmap-rails 2.2.2's Importmap::Map (lib/importmap/map.rb) and
// Importmap::ImportmapTagsHelper for the subset of the config/importmap.rb DSL the reference uses:
// `pin` and `pin_all_from` with `to:`, `under:` and `preload:` (true or false). Anything else in the
// file throws, so a reference bump that needs more of the DSL fails the build instead of drifting.
static class Importmap
{
    // Map#expanded_packages_and_directories: pins in the order they were drawn, then every
    // directory expanded into them. Ruby hashes keep a key's first position when it's reassigned.
    public static List<ImportmapPin> Expand(string importmapRb, string railsRoot)
    {
        var packages = new List<ImportmapPin>();
        var directories = new List<Directive>();
        foreach (var line in File.ReadAllLines(importmapRb))
        {
            if (Directive.Parse(line) is not { } directive)
            {
                continue;
            }

            if (directive.Command == "pin")
            {
                Insert(packages, new ImportmapPin(directive.Argument, directive.To ?? $"{directive.Argument}.js", directive.Preload));
            }
            else
            {
                var existing = directories.FindIndex(d => d.Argument == directive.Argument);
                if (existing >= 0)
                {
                    directories[existing] = directive;
                }
                else
                {
                    directories.Add(directive);
                }
            }
        }

        foreach (var directory in directories)
        {
            var root = Path.Combine(railsRoot, directory.Argument);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var filename in JavascriptFilesInTree(root))
            {
                Insert(packages, new ImportmapPin(ModuleName(filename, directory.Under), ModulePath(filename, directory), directory.Preload));
            }
        }

        return packages;
    }

    // ImportmapTagsHelper#javascript_importmap_tags for the "application" entry point, with no CSP
    // nonce (the reference configures no content security policy). `resolve` is path_to_asset; it
    // returns null for a missing asset, which importmap-rails rescues and skips.
    public static string Tags(List<ImportmapPin> pins, Func<string, string?> resolve)
    {
        var preloads = new List<string>();
        foreach (var pin in pins.Where(pin => pin.Preload))
        {
            if (resolve(pin.Path) is { } path && !preloads.Contains(path))
            {
                preloads.Add(path);
            }
        }

        return string.Join('\n',
            $"<script type=\"importmap\" data-turbo-track=\"reload\">{Json(pins, resolve)}</script>",
            string.Join('\n', preloads.Select(path => $"<link rel=\"modulepreload\" href=\"{Html.Escape(path)}\">")),
            "<script type=\"module\">import \"application\"</script>");
    }

    // Map#to_json: JSON.pretty_generate({ "imports" => resolved paths }). No pin asks for integrity.
    public static string Json(List<ImportmapPin> pins, Func<string, string?> resolve)
    {
        var imports = pins
            .Select(pin => (pin.Name, Path: resolve(pin.Path)))
            .Where(import => import.Path is not null)
            .Select(import => $"    {RubyJson.String(import.Name)}: {RubyJson.String(import.Path!)}")
            .ToList();
        return imports.Count == 0
            ? "{\n  \"imports\": {}\n}"
            : $"{{\n  \"imports\": {{\n{string.Join(",\n", imports)}\n  }}\n}}";
    }

    static void Insert(List<ImportmapPin> packages, ImportmapPin pin)
    {
        var existing = packages.FindIndex(p => p.Name == pin.Name);
        if (existing >= 0)
        {
            packages[existing] = pin;
        }
        else
        {
            packages.Add(pin);
        }
    }

    // [under, filename.chomp(extname).remove(/(?:\/|^)index$/).presence].compact.join("/")
    static string ModuleName(string filename, string? under)
    {
        var stem = filename[..^RubyPath.Extname(filename).Length];
        if (stem == "index")
        {
            stem = "";
        }
        else if (stem.EndsWith("/index", StringComparison.Ordinal))
        {
            stem = stem[..^"/index".Length];
        }

        return string.Join('/', new[] { under, stem.Length == 0 ? null : stem }.OfType<string>());
    }

    // [to || under, filename].compact.reject(&:empty?).join("/")
    static string ModulePath(string filename, Directive directory) =>
        string.Join('/', new[] { directory.To ?? directory.Under, filename }.Where(part => !string.IsNullOrEmpty(part)));

    // Dir[path.join("**/*.js{,m}")].sort: .js and .jsm files, skipping dotfiles and dot-directories,
    // sorted by their full path as strings. Returned relative to `root`.
    static List<string> JavascriptFilesInTree(string root)
    {
        var files = new List<string>();
        Collect(root);
        files.Sort(StringComparer.Ordinal);
        return files.Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')).ToList();

        void Collect(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    Collect(entry);
                }
                else if (name.EndsWith(".js", StringComparison.Ordinal) || name.EndsWith(".jsm", StringComparison.Ordinal))
                {
                    files.Add(entry);
                }
            }
        }
    }

    // One `pin` or `pin_all_from` line: `pin "name", to: "file.js", preload: false # comment`.
    sealed record Directive(string Command, string Argument, string? To, string? Under, bool Preload)
    {
        public static Directive? Parse(string line)
        {
            var reader = new Reader(line.Trim());
            if (reader.AtEnd)
            {
                return null;
            }

            var command = reader.Word();
            if (command is not ("pin" or "pin_all_from"))
            {
                throw Unsupported(line);
            }

            var argument = reader.String() ?? throw Unsupported(line);
            string? to = null, under = null;
            var preload = true;
            while (!reader.AtEnd)
            {
                reader.Expect(',');
                var key = reader.Word();
                reader.Expect(':');
                switch (key)
                {
                    case "to":
                        to = reader.String() ?? throw Unsupported(line);
                        break;
                    case "under":
                        under = reader.String() ?? throw Unsupported(line);
                        break;
                    case "preload":
                        preload = reader.Word() switch
                        {
                            "true" => true,
                            "false" => false,
                            _ => throw Unsupported(line),
                        };
                        break;
                    default:
                        throw Unsupported(line);
                }
            }

            // `pin` takes no `under:` keyword (Ruby raises ArgumentError).
            if (command == "pin" && under is not null)
            {
                throw Unsupported(line);
            }

            return new Directive(command, argument, to, under, preload);
        }

        static FormatException Unsupported(string line) => new($"config/importmap.rb: unsupported line {line}");
    }

    // A cursor over one line of Ruby; a `#` outside a string ends it.
    sealed class Reader(string text)
    {
        int position;

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return position >= text.Length || text[position] == '#';
            }
        }

        public string Word()
        {
            SkipSpace();
            var start = position;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
            {
                position++;
            }

            return text[start..position];
        }

        // A single- or double-quoted string with no escapes or interpolation.
        public string? String()
        {
            SkipSpace();
            if (position >= text.Length || text[position] is not ('"' or '\''))
            {
                return null;
            }

            var quote = text[position];
            var end = text.IndexOf(quote, position + 1);
            if (end < 0)
            {
                return null;
            }

            var value = text[(position + 1)..end];
            if (value.Contains('\\', StringComparison.Ordinal) || quote == '"' && value.Contains("#{", StringComparison.Ordinal))
            {
                return null;
            }

            position = end + 1;
            return value;
        }

        public void Expect(char expected)
        {
            SkipSpace();
            if (position >= text.Length || text[position] != expected)
            {
                throw new FormatException($"config/importmap.rb: expected '{expected}' in {text}");
            }

            position++;
        }

        void SkipSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }
    }
}

// ERB::Util.html_escape, as tag helpers apply it to attribute values.
static class Html
{
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
