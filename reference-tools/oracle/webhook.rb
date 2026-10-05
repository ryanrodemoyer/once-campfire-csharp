# Oracle for crates/campfire/src/integrations/webhook: runs Webhook#deliver against a local
# server for each reply in webhook_cases.json, with the message, payload and replies stubbed
# out, and records the request as sent and what the webhook did with the response.
#   parity/bin/reference runner reference-tools/oracle/webhook.rb
# It reads vectors/webhook/cases.json and writes expected.json next to it, or into $OUT (the app
# logs to stdout).
require "socket"
require "zlib"
require "base64"

dir = File.expand_path("../../vectors/webhook", __dir__)
output_dir = ENV.fetch("OUT", dir)
cases = JSON.parse(File.read(File.join(dir, "cases.json")))

$requests = []
server = TCPServer.new("127.0.0.1", 0)
port = server.addr[1]
Thread.new do
  loop do
    Thread.new(server.accept) do |client|
      request_line = client.gets
      headers = []
      while (line = client.gets) && line != "\r\n"
        headers << line.chomp.split(": ", 2)
      end
      length = headers.find { |name, _| name.casecmp?("content-length") }&.last.to_i
      body = client.read(length)
      $requests << { request_line: request_line.chomp, headers: headers, body: body }
      c = cases.find { |c| request_line.split(" ")[1] == "/#{c["name"]}" }
      sleep c["delay"] if c["delay"]
      reply = c["body_b64"] ? Base64.decode64(c["body_b64"]) : c["body"].to_s.b
      reply = Zlib.gzip(reply) if c["gzip"]
      out = +"HTTP/1.1 #{c["status"]} Status\r\n"
      c["headers"].each { |name, value| out << "#{name}: #{value}\r\n" }
      out << "Content-Encoding: gzip\r\n" if c["gzip"]
      out << "Content-Length: #{reply.bytesize}\r\nConnection: close\r\n\r\n"
      client.write(out)
      client.write(reply)
    rescue Errno::EPIPE, Errno::ECONNRESET
    ensure
      client.close
    end
  end
end

ActiveStorage::Blob.define_singleton_method(:create_and_upload!) do |io:, filename:, content_type:|
  { "filename" => filename, "content_type" => content_type, "body_b64" => Base64.strict_encode64(io.string) }
end

results = cases.map do |c|
  $requests = []
  webhook = Webhook.new(url: c["url"] || "http://127.0.0.1:#{port}/#{c["name"]}")
  reply = nil
  webhook.define_singleton_method(:payload) { |_message| %({"message":"hi"}) }
  webhook.define_singleton_method(:receive_text_reply_to) { |_room, text:| reply = { "text_b64" => Base64.strict_encode64(text), "encoding" => text.encoding.to_s, "valid" => text.valid_encoding? } }
  webhook.define_singleton_method(:receive_attachment_reply_to) { |_room, attachment:| reply = { "attachment" => attachment } }
  message = Struct.new(:room).new(nil)

  outcome = begin
    response = webhook.deliver(message)
    { "status" => response.is_a?(Net::HTTPResponse) ? response.code.to_i : nil, "reply" => reply }
  rescue => e
    { "error" => e.class.name, "reply" => reply }
  end
  outcome.merge("name" => c["name"], "requests" => $requests.map { |r| r.except(:body).merge(body: r[:body]) })
end

File.write(File.join(output_dir, "expected.json"), JSON.pretty_generate(results) + "\n")
