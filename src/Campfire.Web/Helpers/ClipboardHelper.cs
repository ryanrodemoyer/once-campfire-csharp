namespace Campfire.Web.Helpers;

// reference/app/helpers/clipboard_helper.rb, drop_target_helper.rb, emoji_helper.rb,
// forms_helper.rb, qr_code_helper.rb, rich_text_helper.rb, searches_helper.rb, time_helper.rb
public partial class View
{
    /// <summary><c>EmojiHelper::REACTIONS</c>, in order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Reactions { get; } =
    [
        new("👍", "Thumbs up"),
        new("👏", "Clapping"),
        new("👋", "Waving hand"),
        new("💪", "Muscle"),
        new("❤️", "Red heart"),
        new("😂", "Face with tears of joy"),
        new("🎉", "Party popper"),
        new("🔥", "Fire"),
    ];

    /// <summary><c>button_to_copy_to_clipboard(url) do ... end</c>.</summary>
    public static IHtml ButtonToCopyToClipboard(string url, Action body) => Tag.Button(CopyToClipboardOptions(url), body);

    /// <summary><c>button_to_copy_to_clipboard(url) { content }</c> with the content already rendered.</summary>
    public static SafeString ButtonToCopyToClipboard(string url, object? content) => Tag.Button(content, CopyToClipboardOptions(url));

    /// <summary><c>drop_target_actions</c>.</summary>
    public static string DropTargetActions() =>
        "dragenter->drop-target#dragenter dragover->drop-target#dragover drop->drop-target#drop";

    /// <summary><c>auto_submit_form_with(**attributes)</c>: <c>form_with</c> with the auto-submit controller.</summary>
    public SafeString AutoSubmitFormWith(HtmlOptions? attributes = null, FormModel? model = null, string? url = null) =>
        FormWith(model, url, AutoSubmitAttributes(attributes));

    /// <summary><c>auto_submit_form_with(**attributes) do |form| ... end</c>.</summary>
    public IHtml AutoSubmitFormWith(HtmlOptions? attributes, Action<FormBuilder> body, FormModel? model = null, string? url = null) =>
        FormWith(model, url, AutoSubmitAttributes(attributes), body);

    /// <summary><c>link_to_zoom_qr_code(url) do ... end</c>.</summary>
    public static IHtml LinkToZoomQrCode(string url, Action body)
    {
        var id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url)).Replace('+', '-').Replace('/', '_');
        var path = Routes.QrCodePath(id);
        return LinkTo(path, new()
        {
            { "class", "btn" },
            { "data", new HtmlOptions { { "lightbox_target", "image" }, { "action", "lightbox#open" }, { "lightbox_url_value", path } } },
        }, body);
    }

    /// <summary><c>rich_text_data_actions</c>.</summary>
    public static string RichTextDataActions() =>
        "lexxy:change->typing-notifications#start keydown->composer#submitByKeyboard:capture";

    /// <summary><c>mention_prompt_tag(room)</c>.</summary>
    public static SafeString MentionPromptTag(object roomId) =>
        Tag.Element("lexxy_prompt", content: null, options: new HtmlOptions
        {
            { "trigger", "@" },
            { "name", "mention" },
            { "src", Routes.AutocompletableUsersPath(new() { { "room_id", roomId } }) },
            { "remote-filtering", true },
            { "empty-results", "No matches" },
        });

    /// <summary><c>search_results_tag do ... end</c>.</summary>
    public static IHtml SearchResultsTag(Action body) =>
        Tag.Div(new()
        {
            { "id", "search-results" },
            { "class", "messages searches__results" },
            {
                "data", new HtmlOptions
                {
                    { "controller", "search-results" },
                    { "search_results_target", "messages" },
                    { "search_results_me_class", "message--me" },
                    { "search_results_threaded_class", "message--threaded" },
                    { "search_results_mentioned_class", "message--mentioned" },
                    { "search_results_formatted_class", "message--formatted" },
                }
            },
        }, body);

    /// <summary>
    /// <c>local_datetime_tag(datetime, style:, **attributes)</c>: a <c>&lt;time&gt;</c> the
    /// local-time controller fills in.
    /// </summary>
    public static SafeString LocalDatetimeTag(DateTimeOffset datetime, string style = "time", HtmlOptions? attributes = null) =>
        Tag.Time(null, (attributes ?? []).Merge(new HtmlOptions
        {
            { "datetime", Campfire.RailsCompat.Formatting.TimeFormats.Iso8601(datetime) },
            { "data", new HtmlOptions { { "local_time_target", style } } },
        }));

    static HtmlOptions CopyToClipboardOptions(string url) => new()
    {
        { "class", "btn" },
        {
            "data", new HtmlOptions
            {
                { "controller", "copy-to-clipboard" },
                { "action", "copy-to-clipboard#copy" },
                { "copy_to_clipboard_success_class", "btn--success" },
                { "copy_to_clipboard_content_value", url },
            }
        },
    };

    static HtmlOptions AutoSubmitAttributes(HtmlOptions? attributes)
    {
        var options = (attributes ?? []).Clone();
        var data = (options.Delete("data") as HtmlOptions)?.Clone() ?? [];
        data["controller"] = $"auto-submit {RubyValues.ToS(data["controller"])}".Trim();
        options["data"] = data;
        return options;
    }
}
