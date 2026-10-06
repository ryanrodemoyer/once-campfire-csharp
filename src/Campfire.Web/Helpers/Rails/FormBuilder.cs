namespace Campfire.Web.Helpers.Rails;

/// <summary>
/// <c>ActionView::Helpers::FormBuilder</c>: the <c>form</c> in <c>form_with ... do |form|</c>.
/// Fields are named <c>scope[method]</c>, get the id <c>scope_method</c> and read their value from
/// the model, through the field classes in actionview/lib/action_view/helpers/tags/.
/// reference: actionview/lib/action_view/helpers/form_helper.rb (FormBuilder)
/// </summary>
public sealed class FormBuilder
{
    readonly View view;
    readonly HtmlOptions defaultOptions;
    readonly FormBuilder? parent;

    internal FormBuilder(string? objectName, FormModel? model, View view, HtmlOptions options, FormBuilder? parent = null)
    {
        ObjectName = objectName;
        Model = model;
        this.view = view;
        this.parent = parent;
        defaultOptions = options.Slice("index", "namespace", "skip_default_ids", "allow_method_names_outside_object");
        Index = options["index"] ?? options["child_index"];
    }

    /// <summary><c>object_name</c>: the scope fields are named under.</summary>
    public string? ObjectName { get; }

    /// <summary><c>object</c>.</summary>
    public FormModel? Model { get; }

    /// <summary><c>index</c>.</summary>
    public object? Index { get; }

    /// <summary><c>multipart?</c>: set once a file field is rendered, here or in a nested builder.</summary>
    public bool IsMultipart { get; private set; }

    public SafeString TextField(string method, HtmlOptions? options = null) => new TextFieldTag("text", ObjectName, method, Objectify(options)).Render();

    public SafeString EmailField(string method, HtmlOptions? options = null) => new TextFieldTag("email", ObjectName, method, Objectify(options)).Render();

    public SafeString UrlField(string method, HtmlOptions? options = null) => new TextFieldTag("url", ObjectName, method, Objectify(options)).Render();

    public SafeString SearchField(string method, HtmlOptions? options = null) => new TextFieldTag("search", ObjectName, method, Objectify(options)).Render();

    public SafeString TelephoneField(string method, HtmlOptions? options = null) => new TextFieldTag("tel", ObjectName, method, Objectify(options)).Render();

    public SafeString NumberField(string method, HtmlOptions? options = null) => new TextFieldTag("number", ObjectName, method, Objectify(options)).Render();

    /// <summary><c>password_field</c>: never shows the model's value.</summary>
    public SafeString PasswordField(string method, HtmlOptions? options = null)
    {
        var withValue = new HtmlOptions { { "value", null } }.Merge(Objectify(options));
        return new TextFieldTag("password", ObjectName, method, withValue).Render();
    }

    public SafeString HiddenField(string method, HtmlOptions? options = null) => new TextFieldTag("hidden", ObjectName, method, Objectify(options)).Render();

    /// <summary><c>file_field</c>: marks the form multipart.</summary>
    public SafeString FileField(string method, HtmlOptions? options = null)
    {
        MarkMultipart();
        var objectified = Objectify(options);
        // multiple_file_field_include_hidden (load_defaults 7.0): a multiple field sends an empty value too.
        var includeHidden = objectified.ContainsKey("include_hidden") ? objectified.Delete("include_hidden") : true;
        objectified.Delete("direct_upload");
        var field = new TextFieldTag("file", ObjectName, method, objectified);
        if (TagHelper.IsTruthy(objectified["multiple"]) && TagHelper.IsTruthy(includeHidden))
        {
            var name = field.DefaultName(objectified.Clone());
            var hidden = TagHelper.Tag("input", new() { { "name", name }, { "type", "hidden" }, { "value", "" } });
            return OutputSafety.Concat(hidden, field.Render());
        }
        return field.Render();
    }

    /// <summary><c>textarea</c> / <c>text_area</c>.</summary>
    public SafeString TextArea(string method, HtmlOptions? options = null) => new TextAreaTag(ObjectName, method, Objectify(options)).Render();

    /// <summary><c>checkbox(method, options, checked_value = "1", unchecked_value = "0")</c>.</summary>
    public SafeString CheckBox(string method, HtmlOptions? options = null, object? checkedValue = null, object? uncheckedValue = null) =>
        new CheckBoxTag(ObjectName, method, Objectify(options), checkedValue ?? "1", uncheckedValue ?? "0").Render();

    /// <summary>
    /// <c>button(options) do ... end</c>: <c>button_tag</c> with the block as its content.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Called on the builder, like form.button in Ruby")]
    public IHtml Button(HtmlOptions? options, Action body) => View.ButtonTag(ButtonOptions(options), body);

    /// <summary><c>button(value, options)</c>; a null value is the submit default ("Create Message").</summary>
    public SafeString Button(object? value = null, HtmlOptions? options = null) =>
        View.ButtonTag(value ?? SubmitDefaultValue(), ButtonOptions(options));

    /// <summary>
    /// <c>fields_for(record_name, record_object) do |fields| ... end</c> for a plain nested object
    /// (not <c>accepts_nested_attributes_for</c>): fields named <c>scope[record_name][method]</c>.
    /// </summary>
    public IHtml FieldsFor(string recordName, FormModel? model, Action<FormBuilder> body, HtmlOptions? options = null)
    {
        var fieldsOptions = (options ?? []).Clone();
        fieldsOptions["namespace"] = defaultOptions["namespace"];
        var name = Index is not null ? $"{ObjectName}[{RubyValues.ToS(Index)}][{recordName}]" : $"{ObjectName}[{recordName}]";
        fieldsOptions["child_index"] = Index;

        // FormHelper#fields_for: method names must exist on the nested object.
        var builderOptions = new HtmlOptions { { "model", null }, { "allow_method_names_outside_object", false }, { "skip_default_ids", false } }.Merge(fieldsOptions);
        var builder = new FormBuilder(name, model, view, builderOptions, this);
        return new FieldsBlock(builder, body);
    }

    /// <summary><c>field_id(method, *suffixes)</c>.</summary>
    public string FieldId(string method, params string[] suffixes) =>
        View.FieldId(ObjectName, method, suffixes, Index, defaultOptions["namespace"] as string);

    /// <summary><c>field_name(method)</c>.</summary>
    public string FieldName(string method, bool multiple = false) =>
        View.FieldName(ObjectName, method, multiple, Index);

    void MarkMultipart()
    {
        IsMultipart = true;
        parent?.MarkMultipart();
    }

    HtmlOptions Objectify(HtmlOptions? options)
    {
        var result = defaultOptions.Merge(options);
        result["object"] = Model;
        return result;
    }

    static HtmlOptions ButtonOptions(HtmlOptions? options)
    {
        var result = (options ?? []).Clone();
        var formMethod = result["formmethod"];
        if (RubyValues.IsPresent(formMethod) &&
            !System.Text.RegularExpressions.Regex.IsMatch(RubyValues.ToS(formMethod), "post|get", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !result.ContainsKey("name") && !result.ContainsKey("value"))
        {
            result["formmethod"] = "post";
            result["name"] = "_method";
            result["value"] = formMethod;
        }
        return result;
    }

    // submit_default_value with actionview's English locale.
    string SubmitDefaultValue()
    {
        if (Model is null)
        {
            return $"Save {Humanize(ObjectName ?? "")}";
        }
        var modelName = Model.Record.ModelName;
        var human = Humanize(RecordIdentifier.Underscore(modelName[(modelName.LastIndexOf(':') + 1)..]));
        return $"{(Model.IsPersisted ? "Update" : "Create")} {human}";
    }

    // ActiveSupport::Inflector.humanize for plain words.
    static string Humanize(string word)
    {
        var text = word.EndsWith("_id", StringComparison.Ordinal) ? word[..^3] : word;
        text = text.Replace('_', ' ').TrimStart();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    sealed class FieldsBlock(FormBuilder builder, Action<FormBuilder> body) : IHtml
    {
        public void WriteTo(HtmlWriter writer) => body(builder);
    }
}
