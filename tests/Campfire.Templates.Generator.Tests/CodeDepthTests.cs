namespace Campfire.Templates.Generator.Tests;

public sealed class CodeDepthTests
{
    [Theory]
    [InlineData("count", 0)]
    [InlineData("Wrap(() => { ", 2)]
    [InlineData("Labelled(x, label => {", 2)]
    [InlineData(" }) ", -2)]
    [InlineData("if (a) {", 1)]
    [InlineData("} else {", 0)]
    [InlineData("items[0]", 0)]
    [InlineData("Wrap(\"({[\", () => {", 2)]
    [InlineData("Wrap('{', () => {", 2)]
    [InlineData("Wrap(@\"a\"\"{\", () => {", 2)]
    [InlineData("Wrap($\"{(a ? \"}\" : \"{\")}\", () => {", 2)]
    [InlineData("Wrap($\"{{\", () => {", 2)]
    [InlineData("Wrap(\"\"\"\n { \" \"\" \n\"\"\", () => {", 2)]
    [InlineData("Wrap($$\"\"\"{{a}} { \"\"\", () => {", 2)]
    [InlineData("Wrap(() => { // }) \n", 2)]
    [InlineData("Wrap(/* }) */ () => {", 2)]
    [InlineData("Wrap('\\'', () => {", 2)]
    public void Counts_brackets_outside_strings_and_comments(string code, int expected)
    {
        Assert.Equal(expected, CodeDepth.Delta(code));
    }
}
