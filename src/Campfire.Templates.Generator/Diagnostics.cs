using Microsoft.CodeAnalysis;

namespace Campfire.Templates.Generator;

#pragma warning disable RS2008 // The generator ships with the solution, not as a package with release notes.
static class Diagnostics
{
    static readonly string Category = "Campfire.Templates";

    public static readonly DiagnosticDescriptor TemplateNotFound = new(
        "CFT001",
        "ERB template not found",
        "No AdditionalFiles item ends with '{0}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TemplateAmbiguous = new(
        "CFT002",
        "ERB template path is ambiguous",
        "More than one AdditionalFiles item ends with '{0}': {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidMethod = new(
        "CFT003",
        "Invalid ERB template method",
        "[ErbTemplate] method '{0}' {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnclosedBlock = new(
        "CFT004",
        "Unclosed block expression",
        "The block expression on line {1} of '{0}' is never closed by a code tag such as <% }}) %>",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
#pragma warning restore RS2008
