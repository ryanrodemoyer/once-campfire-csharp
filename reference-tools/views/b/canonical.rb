# The canonical token stream the views-B tests compare: one string per start tag (attributes
# in source order), end tag and text run (whitespace collapsed), with authenticity tokens
# masked as «csrf». crates/views/tests/messages_support/mod.rs builds the same stream from the
# Rust output.
module ViewsB
  module Canonical
    VOID = %w[ area base br col embed hr img input keygen link meta source track wbr ].freeze

    def self.tokens(html, document:)
      root = document ? Nokogiri::HTML5(html) : Nokogiri::HTML5.fragment(html)
      out = []
      nodes = document ? [ root.root ] : root.children
      nodes.each { |node| walk(node, out) }
      merge_text(out)
    end

    def self.walk(node, out)
      case node
      when Nokogiri::XML::Element
        attrs = node.attribute_nodes.map { |a| [ a.name, mask(node, a.name, a.value) ] }
        out << "<#{node.name}#{attrs.map { |k, v| %( #{k}="#{escape(v)}") }.join}>"
        node.children.each { |child| walk(child, out) }
        out << "</#{node.name}>" unless VOID.include?(node.name)
      when Nokogiri::XML::Text, Nokogiri::XML::CDATA
        out << "##{node.text.gsub(/[ \t\n\r\f]+/, " ")}"
      end
    end

    def self.mask(node, name, value)
      if (node.name == "input" && node["name"] == "authenticity_token" && name == "value") ||
          (node.name == "meta" && node["name"] == "csrf-token" && name == "content")
        "«csrf»"
      else
        value
      end
    end

    def self.escape(value)
      value.gsub("&", "&amp;").gsub('"', "&quot;").gsub("<", "&lt;")
    end

    def self.merge_text(tokens)
      tokens.each_with_object([]) do |token, merged|
        if token.start_with?("#") && merged.last&.start_with?("#")
          merged[-1] = "#" + (merged.last[1..] + token[1..]).gsub(/ +/, " ")
        else
          merged << token
        end
      end.reject { |token| token == "#" }
    end
  end
end
