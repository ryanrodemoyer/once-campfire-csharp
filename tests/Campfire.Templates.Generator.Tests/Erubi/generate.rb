# Regenerates the Erubi golden files the template generator is tested against:
#
#   ../Corpus/<case>.html       each ../Corpus/<case>.rb.erb rendered by ActionView
#   reference-views.json        the text/code/expression stream ActionView's Erubi produces for
#                               every reference/app/views/**/*.erb
#
# The reference pins rails@1a02651 (8.2.0.alpha) and erubi 1.13.1. Run with erubi 1.13.1 and
# actionview 8.1.4: its template/handlers/erb.rb, erb/erubi.rb and buffers.rb are byte-identical to
# the pinned commit's.
#
#   gem install erubi -v 1.13.1 && gem install actionview -v 8.1.4
#   ruby tests/Campfire.Templates.Generator.Tests/Erubi/generate.rb
gem "erubi", "1.13.1"
gem "actionview", "8.1.4"
require "json"
require "action_view"

Encoding.default_external = Encoding::UTF_8
Encoding.default_internal = nil

here = __dir__
root = File.expand_path("../../..", here)
TemplateStub = Struct.new(:type, :format)
HTML = TemplateStub.new("text/html", :html)

# Records what Erubi hands to the ActionView subclass, before it buffers newlines.
class TracingErubi < ActionView::Template::Handlers::ERB::Erubi
  def self.ops = Thread.current[:erubi_ops]

  private
    def add_text(text)
      TracingErubi.ops << ["text", text.dup] unless text.empty?
      super
    end

    def add_code(code)
      TracingErubi.ops << ["code", code.dup]
      super
    end

    def add_expression(indicator, code)
      TracingErubi.ops << [indicator == "==" ? "raw" : "expr", code.dup]
      super
    end
end

def compile(source, implementation = ActionView::Template::Handlers::ERB::Erubi)
  handler = ActionView::Template::Handlers::ERB
  previous = handler.erb_implementation
  handler.erb_implementation = implementation
  handler.call(HTML, source)
ensure
  handler.erb_implementation = previous
end

def merge_text(ops)
  ops.each_with_object([]) do |op, merged|
    if op[0] == "text" && merged.last&.first == "text"
      merged.last[1] += op[1]
    else
      merged << op
    end
  end
end

# The values the corpus templates use; ../CorpusHarness.cs defines the same in C#.
class CorpusContext
  def initialize
    @output_buffer = ActionView::OutputBuffer.new
  end

  def name = %q(<b>"Tom" & 'Jerry'</b>)
  def safe = "<i>safe</i>".html_safe
  def items = [ 1, 2 ]
  def count = 3
  def nothing = nil
  def flag = true
  def off = false
  def tag_list(*values) = values.join(",")
  def wrap(&block) = "<div>".html_safe + @output_buffer.capture(&block) + "</div>".html_safe
  def labelled(&block) = "<p>".html_safe + @output_buffer.capture("label", &block) + "</p>".html_safe
end

Dir[File.join(here, "../Corpus/*.rb.erb")].sort.each do |path|
  src = compile(File.read(path, mode: "rb").force_encoding(Encoding::UTF_8))
  html = CorpusContext.new.instance_eval(src).to_s
  File.binwrite(path.sub(/\.rb\.erb\z/, ".html"), html)
end

views = File.join(root, "reference/app/views")
golden = Dir[File.join(views, "**/*.erb")].sort.to_h do |path|
  Thread.current[:erubi_ops] = []
  compile(File.read(path, mode: "rb").force_encoding(Encoding::UTF_8), TracingErubi)
  [ path.delete_prefix(views + "/"), merge_text(TracingErubi.ops) ]
end
File.write(File.join(here, "reference-views.json"), JSON.pretty_generate(golden) + "\n")
