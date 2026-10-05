# Oracle for crates/campfire/src/integrations/opengraph: replays opengraph_cases.json against the
# reference's UnfurlLinksController#create logic with fake DNS and a local server standing in for
# the fake public IPs, and records the response plus every DNS lookup and HTTP request made.
#   parity/bin/reference runner crates/campfire/src/integrations/testdata/oracle/opengraph.rb
# It writes ../opengraph_expected.json (the app logs to stdout).
require "socket"
require "zlib"
require "base64"

dir = File.expand_path("..", __dir__)
spec = JSON.parse(File.read(File.join(dir, "opengraph_cases.json")))

$lookups = []
$requests = []
$answers = {}

Resolv.singleton_class.prepend(Module.new do
  define_method(:getaddresses) do |host|
    $lookups << host
    answers = $answers[host] or raise Resolv::ResolvError, "no address for #{host}"
    answers.size > 1 ? answers.shift : answers.first
  end
end)

def body_of(route)
  body = if route["body_b64"] then Base64.decode64(route["body_b64"])
  elsif route["body_repeat"] then route["body_repeat"][0] * route["body_repeat"][1]
  else (route["body"] || "").b
  end
  body = body + " " * (route["pad_to"] - body.bytesize) if route["pad_to"]
  route["gzip"] ? Zlib.gzip(body) : body
end

def respond(client, routes)
  request_line = client.gets or return
  method, target = request_line.split(" ")
  headers = {}
  while (line = client.gets) && line != "\r\n"
    name, value = line.chomp.split(": ", 2)
    headers[name.downcase] = value
  end
  host = headers["host"].to_s.sub(/:\d+\z/, "")
  $requests << [ method, headers["host"], target, headers["accept"], headers["accept-encoding"], headers["user-agent"] ]

  route = routes.find { |r| r["method"] == method && r["host"] == host && r["path"] == target }
  route ||= { "status" => 404, "headers" => [ [ "Content-Type", "text/plain" ] ], "body" => "not found" }
  body = body_of(route)
  out = +"HTTP/1.1 #{route["status"]} Status\r\n"
  route["headers"].each { |name, value| out << "#{name}: #{value}\r\n" }
  out << "Content-Encoding: gzip\r\n" if route["gzip"]
  if route["chunked"]
    out << "Transfer-Encoding: chunked\r\n"
  elsif route["headers"].none? { |name, _| name.casecmp?("content-length") }
    out << "Content-Length: #{body.bytesize}\r\n"
  end
  out << "Connection: close\r\n\r\n"
  client.write(out)
  unless method == "HEAD"
    if route["chunked"]
      body.bytes.each_slice(64 * 1024) { |chunk| client.write("#{chunk.size.to_s(16)}\r\n#{chunk.pack("C*")}\r\n") }
      client.write("0\r\n\r\n")
    else
      client.write(body)
    end
  end
rescue Errno::EPIPE, Errno::ECONNRESET
ensure
  client.close
end

server = TCPServer.new("127.0.0.1", 0)
port = server.addr[1]
Thread.new { loop { Thread.new(server.accept) { |client| respond(client, spec["routes"]) } } }

public_ips = spec["public_ips"]
TCPSocket.singleton_class.prepend(Module.new do
  define_method(:open) do |host, *args, **kw|
    public_ips.include?(host) ? super("127.0.0.1", port, **kw) : super(host, *args, **kw)
  end
end)

results = spec["cases"].map do |c|
  $answers = spec["hosts"].transform_values { |answers| answers.map(&:dup) }
  $lookups = []
  $requests = []

  response = begin
    opengraph = Opengraph::Metadata.from_url(c["url"])
    opengraph.valid? ? { status: 200, body: opengraph.to_json } : { status: 204 }
  rescue => e
    { status: 500, error: e.class.name }
  end
  { name: c["name"], url: c["url"], response: response, lookups: $lookups, requests: $requests }
end

File.write(File.join(dir, "opengraph_expected.json"), JSON.pretty_generate(results) + "\n")
