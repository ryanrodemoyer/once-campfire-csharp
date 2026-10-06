using System.Text.Json;
using Campfire.Data.Schema;
using Campfire.RichText.Attachments;
using Campfire.RichText.Editing;
using Campfire.RichText.Html;
using Campfire.RichText.PlainText;
using Campfire.RichText.Tests.Attachments;
using Campfire.Vectors;
using Microsoft.Data.Sqlite;

namespace Campfire.RichText.Tests.Editing;

/// <summary>
/// Stored → display → edit → save → display → plain text → search index, for every reference body
/// the editor opens. Display, plain text and the editor value of the stored body are each checked
/// against the reference by the vector tests; here the body comes back from the editor unchanged
/// and is saved, and what it then shows and indexes must still be what the reference showed and
/// indexed for the original.
/// </summary>
/// <remarks>
/// "Unchanged" means as Lexxy posts an untouched attachment back: the attributes it was given,
/// with the JSON content it decoded written back as markup. Lexxy itself (the browser side) is
/// covered by the parity harness, not here.
/// </remarks>
public class EditRoundTripTests
{
    /// <summary>
    /// Rails' own serialization drops a newline straight after <c>&lt;pre&gt;</c> or
    /// <c>&lt;textarea&gt;</c>, so its editor value already lacks it (see the case's <c>editable</c>
    /// vector) and the saved text differs from the original as it does in Rails.
    /// </summary>
    static readonly HashSet<string> RailsDropsALeadingNewline = ["malformed pre newline"];

    public static TheoryData<RichTextCase> EditableCases()
    {
        var data = new TheoryData<RichTextCase>();
        foreach (var vector in RichTextVectors.File.Cases)
        {
            if (vector.Editable is { Raised: false, Ok: not null } && !vector.PlainText.Raised && !RailsDropsALeadingNewline.Contains(vector.Name))
            {
                data.Add(vector);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EditableCases))]
    public void Saving_an_untouched_edit_shows_and_indexes_what_the_reference_did(RichTextCase vector)
    {
        var context = VectorRecords.Context(vector.Host);

        var saved = EditableContent.StoredBody(PostedByEditor(EditableContent.EditorValue(vector.Body, context)!));

        Assert.Equal(ExpectedPresentation(vector), MessagePresentation.Present(saved, context));
        Assert.Equal(vector.PlainText.Ok, RichTextPlainText.PlainTextBody(saved, null, context));
    }

    [Fact]
    public void Covers_every_editable_reference_case()
    {
        Assert.Equal(546, EditableCases().Count + RailsDropsALeadingNewline.Count);
    }

    [Theory]
    [InlineData("lexxy mention", "David", "how")]
    [InlineData("trix mention", "David", "Hey")]
    [InlineData("lexxy everything", "Kevin", "quote")]
    [InlineData("trix unfurl with text", null, "basecamp")]
    public void A_saved_edit_is_found_by_its_text_and_mentions_but_not_its_markup(string name, string? mentioned, string word)
    {
        var vector = RichTextVectors.File.Cases.Single(c => c.Name == name);
        var context = VectorRecords.Context(vector.Host);
        var saved = EditableContent.StoredBody(PostedByEditor(EditableContent.EditorValue(vector.Body, context)!));

        using var index = new SearchIndex();
        index.Insert(1, RichTextPlainText.PlainTextBody(vector.Body, null, context));
        index.Update(1, RichTextPlainText.PlainTextBody(saved, null, context));

        Assert.Equal([1L], index.Search(word));
        if (mentioned is not null)
        {
            Assert.Equal([1L], index.Search(mentioned));
        }
        Assert.Empty(index.Search("sgid"));
        Assert.Empty(index.Search("span"));
        Assert.Empty(index.Search("lexxy"));
    }

    // Ported from reference/test/models/message/searchable_test.rb
    [Fact]
    public void Rich_text_body_is_converted_to_plain_text_for_indexing()
    {
        using var index = new SearchIndex();
        index.Insert(1, RichTextPlainText.PlainTextBody("<span>My hovercraft is full of eels</span>", null, VectorRecords.Context(null)));

        Assert.Empty(index.Search("span"));
        Assert.Equal([1L], index.Search("eel"));
    }

    /// <summary>
    /// The body Lexxy posts for an untouched editor: each attachment keeps the attributes it was
    /// given, with the content the editor decoded from JSON.
    /// </summary>
    static string PostedByEditor(string editorValue)
    {
        var fragment = HtmlParser.ParseFragment(editorValue);
        foreach (var node in RichTextPlainText.AttachmentNodes(fragment))
        {
            if (node.GetAttribute("content") is { } json && json.StartsWith('"'))
            {
                node.SetAttribute("content", JsonSerializer.Deserialize<string>(json)!);
            }
        }
        return fragment.ToHtml();
    }

    static Presentation ExpectedPresentation(RichTextCase vector) =>
        vector.Presentation.Raised ? new Presentation.Unrenderable() : new Presentation.Html(vector.Presentation.Ok!);

    /// <summary><c>message_search_index</c> as the reference schema creates it, kept as <c>Message::Searchable</c> keeps it.</summary>
    sealed class SearchIndex : IDisposable
    {
        readonly SqliteConnection connection = new("Data Source=:memory:");

        public SearchIndex()
        {
            connection.Open();
            Execute(RailsSchema.Statements.Single(s => s.Contains("message_search_index", StringComparison.Ordinal)));
        }

        public void Insert(long id, string body) =>
            Execute("insert into message_search_index(rowid, body) values (@id, @body)", id, body);

        public void Update(long id, string body) =>
            Execute("update message_search_index set body = @body where rowid = @id", id, body);

        // The scope `search`: "idx.body match ?"
        public List<long> Search(string query)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select rowid from message_search_index where body match @query order by rowid";
            command.Parameters.AddWithValue("@query", query);
            using var reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
            return ids;
        }

        void Execute(string sql, long? id = null, string? body = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null)
            {
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@body", body);
            }
            command.ExecuteNonQuery();
        }

        public void Dispose() => connection.Dispose();
    }
}
