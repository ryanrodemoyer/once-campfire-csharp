namespace Campfire.Web.Helpers;

/// <summary>
/// A sound <c>/play name</c> plays (reference/app/models/sound.rb): an mp3 asset shown as an image
/// or a line of text.
/// </summary>
public sealed record Sound(string Name, SoundImage? Image, string? Text)
{
    /// <summary><c>asset_path</c>: the mp3, relative to the asset load path.</summary>
    public string AssetPath => $"{Name}.mp3";

    /// <summary><c>Sound.find_by_name(name)</c>.</summary>
    public static Sound? FindByName(string name) => Index.GetValueOrDefault(name);

    static Sound WithImage(string name, string image, int width, int height) => new(name, new SoundImage($"sounds/{image}", width, height), null);

    static Sound WithText(string name, string text) => new(name, null, text);

    /// <summary><c>Sound::BUILTIN</c>, in its order.</summary>
    public static IReadOnlyList<Sound> Builtin { get; } =
    [
        WithImage("56k", "56k.webp", 79, 33),
        WithText("bell", "🔔"),
        WithText("bezos", "😆💭"),
        WithText("bueller", "anyone?"),
        WithText("butts", "👐 🚬"),
        WithImage("clowntown", "clowntown.webp", 210, 150),
        WithText("cottoneyejoe", "🎶🙉🎶 "),
        WithText("crickets", "hears crickets chirping"),
        WithImage("curb", "curb.webp", 150, 101),
        WithText("dadgummit", "dad gummit!! 🎣"),
        WithImage("dangerzone", "dangerzone.webp", 157, 32),
        WithText("danielsan", "🎆 🏆 🎆"),
        WithImage("deeper", "top.webp", 188, 80),
        WithText("ballmer", "developers!"),
        WithImage("donotwant", "donotwant.webp", 150, 150),
        WithImage("drama", "drama.webp", 300, 16),
        WithText("flawless", "#flawless"),
        WithText("glados", "🤖💢"),
        WithText("gogogo", "Go, go, go!"),
        WithImage("greatjob", "greatjob.webp", 79, 16),
        WithText("greyjoy", "😖🎺"),
        WithText("guarantee", "guarantees it 👌"),
        WithText("heygirl", "✨💁✨"),
        WithText("honk", "HONK"),
        WithText("horn", "🐶 ✂️ 🐱"),
        WithText("horror", "💀 💀 💀 💀 💀 💀 💀"),
        WithText("inconceivable", "doesn't think it means what you think it means…"),
        WithText("letitgo", "❄️👩❄️⛄️❄️"),
        WithText("live", "is DOING IT LIVE"),
        WithImage("loggins", "loggins.webp", 200, 151),
        WithText("makeitso", "make it so 👉"),
        WithText("noooo", "👸💀😒"),
        WithImage("nyan", "nyan.webp", 36, 15),
        WithText("ohmy", "raises an eyebrow 😏"),
        WithText("ohyeah", "isn't playing by the rules"),
        WithImage("pushit", "pushit.webp", 104, 15),
        WithText("rimshot", "plays a rimshot"),
        WithText("rollout", "is rolling out 🚗"),
        WithImage("rumble", "rumble.webp", 220, 150),
        WithText("sax", "🌇🎷🎶"),
        WithText("secret", "found a secret area 🔑"),
        WithText("sexyback", "🔞"),
        WithText("story", "and now you know…"),
        WithText("tada", "plays a fanfare 🎏"),
        WithText("tmyk", "✨ ⭐️ The More You Know ✨ ⭐️"),
        WithText("totes", "😁👍"),
        WithText("trololo", "трололо"),
        WithText("trombone", "plays a sad trombone"),
        WithText("unix", "knows this 💻"),
        WithText("vuvuzela", "======<() ~ ♪ ~♫"),
        WithImage("what", "what.webp", 100, 131),
        WithText("whoomp", "👏‼️😎"),
        WithText("wups", "wups!"),
        WithImage("yay", "yay.webp", 103, 50),
        WithImage("yeah", "yeah.webp", 104, 15),
        WithText("yodel", "📣🗻🙉"),
    ];

    // Static initializers run in order, so these come after BUILTIN.
    static readonly Dictionary<string, Sound> Index = Builtin.ToDictionary(sound => sound.Name);

    /// <summary><c>Sound.names</c>: sorted, as Ruby sorts strings (by bytes).</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Builtin.Select(sound => sound.Name).Order(StringComparer.Ordinal)];
}

/// <summary><c>Sound::Image</c>: an image asset under <c>sounds/</c> and its size.</summary>
public sealed record SoundImage(string AssetPath, int Width, int Height);
