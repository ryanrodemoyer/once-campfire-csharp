# Differential fuzz run 2 (Q12)

R04's live differential fuzzer (`tests/Campfire.RichText.Fuzz`), rerun against the reference pinned
in Q11, `campfire-reference:edbc779` (Rails main `e3d5c569d217`, Nokogiri 1.19.4 with libxml2
2.13.9, Loofah 2.25.2). R04's runs were against `345e637`; this one confirms its fixes still hold
and fixes what the new reference changed underneath them.

## What the fuzzer covers

Every case is a stored message body, rendered for one of six request hosts. The oracle
(`Oracle/oracle.rb`) runs it through the reference's own code in a throwaway instance, and the port
computes the same six outputs (`PipelineOutputs`):

| Output | Rails entry point | What it exercises |
| --- | --- | --- |
| `presentation` | `message_presentation(message)` | content filters, the safe-list sanitizer, Loofah's CSS scrubber, attachment rendering (mentions, blobs, embeds, content attachments, located records, missing attachables), `auto_link` |
| `presentation_raised` | the `auto_link(h(filters.apply(...)))` call inside it | what raises and gets rescued |
| `plain_text` | `message.body.to_plain_text` | every attachable's plain-text representation |
| `editable` | `editable_body(message)` then `to_editor_html` | the composer value, through Lexxy's adapter |
| `mentioned` | `Message#mentionees` | SGID resolution of mentions |
| `filtered` | `ContentFilters::TextMessagePresentationFilters.apply(body).to_html` | `RemoveSoloUnfurledLinkText`, `SanitizeTags`, `StyleUnfurledTwitterAvatars` and the sanitizer on their own |

Every presentation, the port's and the reference's, is also checked against the independent
`SecurityAssertions`.

The corpus (`FuzzCorpus`) mixes seven families: the OWASP XSS cheat sheet in every place a body can
carry it, malformed nesting, huge inputs, Unicode edge cases, every SGID variant the oracle mints
(users, rooms, messages, blobs; attachable, tampered, expired, Rails 7 and bare GIDs), mutations of
the 658 reference cases, and random trees.

## Runs

All on one 16-core machine with `CAMPFIRE_FUZZ_WORKERS` oracles side by side, on 2026-10-13
(times UTC). Each line is one `dotnet test tests/Campfire.RichText.Fuzz -c Release --filter Live`
run, with `CAMPFIRE_FUZZ_ITERATIONS` and `CAMPFIRE_FUZZ_SEED` as listed. Fixes went in between them.

| Run | Seed | Cases | Workers | Duration | Disagreements | Security failures (port / reference) | Unanswered |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 06:38 | 1 | 2,000 | 8 | 38 s | 16 (editable 9, plain_text 7, presentation 2) | 0 / 0 | 0 |
| 06:48 | 1 | 2,000 | 8 | 38 s | 3 (plain_text) | 0 / 0 | 0 |
| 06:49 | 1 | 2,000 | 8 | 38 s | 0 | 0 / 0 | 0 |
| 06:50 | 1 | 20,000 | 12 | 3 min 49 s | 25 (presentation 15, filtered 12, presentation_raised 6, plain_text 3, editable 2, mentioned 2) | 0 / 0 | 7 |
| 07:10 | 1 | 20,000 | 10 | 4 min 36 s | 7 (filtered) | 0 / 0 | 7 |
| 07:39 | 1 | 20,000 | 10 | 9 min 2 s | 0 | 0 / 0 | 0 |
| 07:51 | 2 | 200,000 | 12 | 53 min 36 s | 6 (editable 5, presentation 1) | 8 / 8 | 0 |
| 11:59 | 3 | 200,000 | 12 | 48 min 0 s | 1 (filtered) | 5 / 5 | 0 |
| 12:51 | 4 | 20,000 | 12 | 8 min 34 s | 0 | 0 / 0 | 0 |

In total 486,000 cases. The seed 3 run had every fix but the last (10, below) and saw every family:
39,838 owasp, 29,813 nesting, 2,007 huge, 30,131 unicode, 40,048 sgid, 30,126 mutation and
28,037 tree. The seed 4 run is the final code.

## Divergences and their fixes

Each fix has a regression case. `tests/Campfire.RichText.Fuzz/Corpus/regressions.jsonl.gz` holds
the bodies of the disagreements above, as each run's `findings.jsonl` kept them (up to 20 per
family and kind), asked of a fresh reference and recorded with its outputs by
`DifferentialFuzzTests.Record_regression_cases`. Left out are the bodies that attach the fuzzed
message itself (from the runs before fix 2), whose rendering Ruby's stack ends. That is 43 cases,
replayed by `Regression_cases_equal_the_references_outputs` on every `bin/check`; with fixes 8 and
9 reverted, exactly the 6 seed 2 cases fail. Each fix also has a golden-vector or unit test for the
component that was wrong.

1. **Located records in plain text.** An SGID that locates a Message or a Room rendered `☒` in plain
   text. Neither defines `attachable_plain_text_representation`, so `Attachment#to_plain_text` is the
   caption. `DatabaseAttachables` now locates any existing record, and `RichTextPlainText` uses the
   caption for one. Test: `LocatedRecordPlainTextTests`.
2. **Located messages in the presentation.** The oracle attached the fuzzed message to itself,
   which Rails renders until its stack gives out. `oracle.rb` now locates a second message with a
   stored body, and `PipelineOutputs.RenderLocatedMessage` renders that row (the port side of the
   harness, not the app).
3. **`JSON.parse` of SGID payloads.** The port parsed a decoded SGID with `System.Text.Json`,
   which rejects payloads json 2.21.2 accepts, so the port raised where Rails rendered.
   `RubyJsonParser` ports Ruby json's parser, including when its error message can't be logged.
   Test: `RubyJsonParserTests` against `Attachments/Vectors/json_parse.json` (`generate_json.rb`,
   run in the reference).
4. **GlobalID parsing.** globalid 1.4.0 on Ruby 4.0 splits a GID with the RFC 3986 parser, not
   RFC 2396: other hosts, ports and userinfo parse, and a composite id has a size limit
   (`GlobalId.cs`). Test: `GlobalIdParseVectorTests` against `GlobalId/Vectors/parse.json`
   (`generate.rb`).
5. **Style attributes.** The sanitizer's style handling was an allow-list approximation. It is now
   a port of Loofah's `scrub_css` over Crass (`CssScrubber`). Test: `CssScrubberTests` against
   `Sanitize/Vectors/css_scrub.json` (`generate_css.rb`).
6. **The editor value.** `ActionText::RichText.new(body:)` casts the editable body through the
   attribute's coder, one more parse and serialization than loading it (`EditableContent`).
7. **The content filters.** `ActionText::Content::Filter.apply` parses each filter's string result
   again; `RemoveSoloUnfurledLinkText` always computes the plain text and `normalize_tweet_url` keeps
   nil; and an `action-text-attachment` or `action-text-markdown` element is matched by name in any
   namespace (`ContentFilters`, `RichTextRenderer`). `alt` is an allowed attachment attribute
   (`SafeList`).
8. **OpenGraph embed content** (seed 2, 5 cases, `editable`). Rails reads a Lexxy embed's title,
   link, image and description with `Nokogiri::HTML.fragment`
   (`reference/lib/rails_ext/actiontext_opengraph_embeds.rb`), which is libxml2's legacy HTML 4
   parser, not HTML5. On broken markup the two build different trees, so the editor showed a
   different title. `LegacyHtmlParser` ports libxml2 2.13.9's `HTMLparser.c` tree building (and
   `Html4Entities` moved from `Campfire.Jobs` to `Campfire.RichText` for it);
   `AttachmentResolution.ContentDetails` and `ContentFilters`' solo-unfurl check both use it.
   Test: `LegacyHtmlParserTests` against `Html/Vectors/legacy_html.json`
   (`generate_legacy_html.rb`): 3,044 inputs, each the same tree and the same embed details as
   Nokogiri.
9. **Character references in URIs** (seed 2, 1 case, `presentation`). The port's
   `CGI.unescapeHTML` parsed `&#+58;` as `:` (`uint.TryParse` takes a sign and whitespace), so it
   read `javascript&#+58;…` as a `javascript:` URI and dropped an `href` Loofah keeps (no browser
   decodes `&#+58;` either). Digits only now (`SafeListSanitizer`). Test: `AllowedUriTests` against
   `Sanitize/Vectors/allowed_uri.json` (`generate_allowed_uri.rb`): 2,726 URIs, each allowed or
   not as `Loofah::HTML5::Scrub.allowed_uri?` decides.
10. **Control characters in a scrubbed style** (seed 3, 1 case, `filtered`). Loofah's `scrub_css`
    keeps a C0 control as a delimiter, but `PermitScrubber` stores the result with Nokogiri's
    `Attr#value=`, whose libxml2 entity encoding drops C0 controls other than tab, newline and
    carriage return. The port stored the scrubbed style as is (`SafeListSanitizer`). Test:
    `SanitizerSecurityTests.OnlyAllowlistedStylePropertiesSurvive`, with the reference's outputs.

The unanswered cases in the 06:50 and 07:10 runs were huge bodies of links the reference took more
than the two-minute case timeout over (`auto_link`, which the oracle runs twice: about 2.5 minutes
on an idle CPU for 900 KB). `FuzzSettings.CaseTimeout` is now ten minutes, and no later run left a
case unanswered.

## Security assertions

The seed 2 run found 8 cases, and the seed 3 run 5, whose presentation fails the independent security assertions on both
sides: the port reproduces the reference byte for byte, so they are not divergences, and nothing in
the port alone fails them. They are a weakness in the reference's own presentation pipeline. The
details stay out of this public file until the upstream project has had a chance to fix them; Q13
(the second security review) takes them from here, through private disclosure, and the port follows
whatever upstream does.

## Corpus

`tests/Campfire.RichText.Fuzz/Corpus/recorded.jsonl.gz` is re-recorded from the seed 3 run
(`CAMPFIRE_FUZZ_RECORD=4000`): 4,000 cases with the `edbc779` reference's outputs, replayed by
`Recorded_cases_equal_the_references_outputs` on every `bin/check`. R04's corpus was recorded at
`345e637`, so the stale-output exceptions Q11 left in that test are gone.
