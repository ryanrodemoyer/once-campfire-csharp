namespace Campfire.Web.Helpers.Rails;

// ActionView::Helpers::Tags::Base: a field's default name and id and its value from the model.
// reference: actionview/lib/action_view/helpers/tags/base.rb
abstract class FieldTag
{
    protected readonly string objectName;
    protected readonly string methodName;
    protected readonly HtmlOptions options;
    readonly FormModel? model;
    readonly bool skipDefaultIds;
    readonly bool allowMethodNamesOutsideObject;

    protected FieldTag(string? objectName, string methodName, HtmlOptions options)
    {
        if (objectName is not null && (objectName.EndsWith("[]", StringComparison.Ordinal) || objectName.EndsWith("[]]", StringComparison.Ordinal)))
        {
            throw new NotSupportedException("object[] auto-indexed names aren't used by the app");
        }

        this.objectName = objectName ?? "";
        this.methodName = methodName;
        this.options = options.Clone();
        model = this.options.Delete("object") as FormModel;
        skipDefaultIds = TagHelper.IsTruthy(this.options.Delete("skip_default_ids"));
        allowMethodNamesOutsideObject = TagHelper.IsTruthy(this.options.Delete("allow_method_names_outside_object"));

        // Placeholderable: a string placeholder is used as is.
        if (this.options["placeholder"] is { } placeholder && TagHelper.IsTruthy(placeholder) && placeholder is not (string or SafeString))
        {
            throw new NotSupportedException("Translated placeholders aren't used by the app");
        }
    }

    // value: the model's attribute; with allow_method_names_outside_object, nil when it has none.
    protected object? Value()
    {
        if (model is null)
        {
            return null;
        }
        if (model.TryGetAttribute(methodName, out var value))
        {
            return value;
        }
        return allowMethodNamesOutsideObject
            ? null
            : throw new InvalidOperationException($"undefined method '{methodName}' for {model.Record.ModelName}");
    }

    // add_default_name_and_field(options, "id").
    protected void AddDefaultNameAndId(HtmlOptions html)
    {
        var index = NameAndIdIndex(html);
        html["name"] = html.Fetch("name", () => View.FieldName(objectName, SanitizedMethodName, TagHelper.IsTruthy(html["multiple"]), index));
        if (!skipDefaultIds)
        {
            html["id"] = html.Fetch("id", () => View.FieldId(objectName, methodName, index: index, @namespace: html.Delete("namespace") as string));
            if (html.Delete("namespace") is string ns)
            {
                html["id"] = html["id"] is { } id ? $"{ns}_{RubyValues.ToS(id)}" : ns;
            }
        }
    }

    // The name add_default_name_and_id would give, for the hidden field a multiple file field adds.
    internal object? DefaultName(HtmlOptions html)
    {
        AddDefaultNameAndId(html);
        return html["name"];
    }

    string SanitizedMethodName => methodName.EndsWith('?') ? methodName[..^1] : methodName;

    static object? NameAndIdIndex(HtmlOptions html) =>
        html.ContainsKey("index") ? html.Delete("index") ?? "" : null;
}

// Tags::TextField and its subclasses (EmailField, PasswordField, HiddenField, FileField, ...):
// the type is the class name without "Field".
sealed class TextFieldTag(string fieldType, string? objectName, string methodName, HtmlOptions options)
    : FieldTag(objectName, methodName, options)
{
    public SafeString Render()
    {
        var html = options.Clone();
        if (!html.ContainsKey("size"))
        {
            html["size"] = html["maxlength"];
        }
        if (!TagHelper.IsTruthy(html["type"]))
        {
            html["type"] = fieldType;
        }
        if (fieldType != "file")
        {
            html["value"] = html.Fetch("value", Value);
        }
        AddDefaultNameAndId(html);
        return TagHelper.Tag("input", html);
    }
}

// Tags::TextArea.
sealed class TextAreaTag(string? objectName, string methodName, HtmlOptions options)
    : FieldTag(objectName, methodName, options)
{
    public SafeString Render()
    {
        var html = options.Clone();
        AddDefaultNameAndId(html);
        if (html.Delete("size") is string size)
        {
            var parts = size.Split('x');
            html["cols"] = parts[0];
            html["rows"] = parts.Length > 1 ? parts[1] : null;
        }
        var content = html.ContainsKey("value") ? html.Delete("value") : Value();
        return TagHelper.ContentTag("textarea", content, html);
    }
}

// Tags::CheckBox with Checkable.
sealed class CheckBoxTag(string? objectName, string methodName, HtmlOptions options, object checkedValue, object? uncheckedValue)
    : FieldTag(objectName, methodName, options)
{
    public SafeString Render()
    {
        var html = options.Clone();
        html["type"] = "checkbox";
        html["value"] = checkedValue;
        if (IsInputChecked(html))
        {
            html["checked"] = "checked";
        }
        if (TagHelper.IsTruthy(html["multiple"]))
        {
            throw new NotSupportedException("multiple check boxes aren't used by the app");
        }
        AddDefaultNameAndId(html);

        var includeHidden = html.ContainsKey("include_hidden") ? html.Delete("include_hidden") : true;
        var checkbox = TagHelper.Tag("input", html);
        if (!TagHelper.IsTruthy(includeHidden))
        {
            return checkbox;
        }
        return OutputSafety.Concat(HiddenFieldForCheckbox(html), checkbox);
    }

    bool IsInputChecked(HtmlOptions html)
    {
        if (html.ContainsKey("checked"))
        {
            var given = html.Delete("checked");
            return given is true || (given is string text && text == "checked");
        }
        return IsChecked(Value());
    }

    bool IsChecked(object? value) => value switch
    {
        bool flag => flag == TagHelper.IsTruthy(checkedValue),
        null => false,
        string text => text == RubyValues.ToS(checkedValue),
        _ when RubyValues.AsArray(value) is { } array => array.Any(item => Equals(item, checkedValue)),
        _ => ToI(value) == ToI(checkedValue),
    };

    SafeString HiddenFieldForCheckbox(HtmlOptions html)
    {
        if (!TagHelper.IsTruthy(uncheckedValue))
        {
            return SafeString.Empty;
        }
        var hidden = html.Slice("name", "disabled", "form").Merge(new HtmlOptions { { "type", "hidden" }, { "value", uncheckedValue } });
        return TagHelper.Tag("input", hidden);
    }

    static long ToI(object? value) => Campfire.RailsCompat.Ruby.RubyString.ToI(RubyValues.ToS(value));
}
