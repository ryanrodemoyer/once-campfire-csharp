// Views and helpers use the Rails helper types and the template runtime everywhere, as ERB does
// without requires. A template's code sees its declaring file's usings, so these keep every
// declaring file from repeating them.
global using Campfire.Templates;
global using Campfire.Web.Helpers.Rails;
