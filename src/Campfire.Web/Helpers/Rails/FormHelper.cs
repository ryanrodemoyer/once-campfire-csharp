namespace Campfire.Web.Helpers;

// ActionView::Helpers::FormHelper#form_with with the reference's settings: form_with_generates_ids
// on, form_with_generates_remote_forms off.
// reference: actionview/lib/action_view/helpers/form_helper.rb
public partial class View
{
    /// <summary>
    /// <c>form_with(model:, scope:, url:, **options) do |form| ... end</c>. With a model the scope
    /// is its param key and a persisted model patches. A null <paramref name="url"/> posts to
    /// <see cref="CurrentPath"/> (<c>url_for({})</c>) when there is no model; with a model, pass the
    /// URL <c>polymorphic_path</c> would build.
    /// </summary>
    public IHtml FormWith(FormModel? model, string? url, HtmlOptions? options, Action<FormBuilder> body, string? scope = null)
    {
        var (builderOptions, action) = FormWithSetup(model, url, options, ref scope);
        var builder = new FormBuilder(scope, model, this, builderOptions);
        return new FormWithBlock(this, builder, model, action, builderOptions, body);
    }

    /// <summary><c>form_with(...)</c> without a block: the opening form tag and its hidden fields only.</summary>
    public SafeString FormWith(FormModel? model = null, string? url = null, HtmlOptions? options = null, string? scope = null)
    {
        var (builderOptions, action) = FormWithSetup(model, url, options, ref scope);
        var (attributes, extraTags) = FormTagParts(action, HtmlOptionsForFormWith(model, builderOptions));
        return OutputSafety.Concat(TagHelper.Tag("form", attributes, open: true), extraTags);
    }

    (HtmlOptions Options, string Action) FormWithSetup(FormModel? model, string? url, HtmlOptions? options, ref string? scope)
    {
        var builderOptions = new HtmlOptions { { "allow_method_names_outside_object", true }, { "skip_default_ids", false } }.Merge(options);
        if (model is not null)
        {
            if (url is null)
            {
                throw new ArgumentException("form_with with a model needs the URL polymorphic_path would build", nameof(url));
            }
            scope ??= model.Record.ParamKey;
        }
        return (builderOptions, url ?? CurrentPath ?? RequestPath);
    }

    // html_options_for_form_with.
    static HtmlOptions HtmlOptionsForFormWith(FormModel? model, HtmlOptions options)
    {
        var html = options.Slice("id", "class", "multipart", "method", "data", "authenticity_token");
        var extra = options["html"] as HtmlOptions ?? [];
        html = html.Merge(extra);
        var local = options.ContainsKey("local") ? TagHelper.IsTruthy(options["local"]) : true;
        html["remote"] = TagHelper.IsTruthy(extra.Delete("remote")) || !local;
        if (!TagHelper.IsTruthy(html["method"]) && model is { IsPersisted: true })
        {
            html["method"] = "patch";
        }
        if (options.ContainsKey("skip_enforcing_utf8"))
        {
            html["enforce_utf8"] = !TagHelper.IsTruthy(options["skip_enforcing_utf8"]);
        }
        else if (options.ContainsKey("enforce_utf8"))
        {
            html["enforce_utf8"] = options["enforce_utf8"];
        }
        return html;
    }

    // The block form: the body is captured first, since a file field in it makes the form multipart.
    sealed class FormWithBlock(View view, FormBuilder builder, FormModel? model, string action, HtmlOptions options, Action<FormBuilder> body) : IHtml
    {
        public void WriteTo(HtmlWriter writer)
        {
            var output = writer.Capture(() => body(builder));
            if (!TagHelper.IsTruthy(options["multipart"]))
            {
                options["multipart"] = builder.IsMultipart;
            }
            var (attributes, extraTags) = view.FormTagParts(action, HtmlOptionsForFormWith(model, options));
            writer.AppendRaw(TagHelper.ContentTagString("form", OutputSafety.SafeJoin([extraTags, output]), attributes));
        }
    }
}

/// <summary>
/// The model a form is built for: its class and key (for the scope and ids), whether it is
/// persisted (a persisted model's form patches), and the attribute values fields read. A field
/// whose attribute isn't listed has no value, as when the object doesn't respond to the method.
/// </summary>
public sealed class FormModel(RecordKey record, bool isPersisted, IReadOnlyDictionary<string, object?>? attributes = null)
{
    readonly IReadOnlyDictionary<string, object?> attributes = attributes ?? new Dictionary<string, object?>();

    /// <summary>A new record of the model, like <c>Message.new</c>.</summary>
    public static FormModel New(string modelName, IReadOnlyDictionary<string, object?>? attributes = null) =>
        new(RecordKey.New(modelName), isPersisted: false, attributes);

    public RecordKey Record { get; } = record;

    public bool IsPersisted { get; } = isPersisted;

    /// <summary><c>object.respond_to?(name)</c> and <c>object.public_send(name)</c>.</summary>
    public bool TryGetAttribute(string name, out object? value) => attributes.TryGetValue(name, out value);
}
