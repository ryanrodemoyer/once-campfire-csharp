require "json"
require "action_controller/test_case"

HOST = "campfire.test"
NOW = Time.now.utc
WORK = ENV.fetch("PARITY_WORK")
OUTPUT = ARGV.fetch(0, "tests/Campfire.Web.Tests/Controllers/UsersSidebars/Vectors/sidebars.json")

def session_row(id, user_id, token, ip_address)
  "INSERT INTO sessions (id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) " \
    "VALUES (#{id}, #{user_id}, '#{token}', '#{ip_address}', 'curl/8.0', '2026-03-02 16:00:00', '2026-03-01 09:00:00', '2026-03-02 16:00:00')"
end

USERS = [
  { id: 127326141, token: "DavidSessionToken0000001", name: "David" },
  { id: 149087659, token: "JasonSessionToken0000002", name: "Jason" },
  { id: 394959859, token: "BenderSessionToken000003", name: "Bender Bot" },
  { id: 712064548, token: "KevinSessionToken0000004", name: "Kevin" },
  { id: 773523953, token: "JzSessionToken0000000005", name: "JZ" },
  { id: 773523954, token: "RitaSessionToken00000006", name: "Rita Lopez" },
  { id: 773523955, token: "MallorySessionToken00007", name: "Mallory Banned" },
  { id: 773523956, token: "DeployBotSessionToken008", name: "Deploy Bot" },
  { id: 773523957, token: "OldBotSessionToken000009", name: "Old Bot" },
  { id: 773523958, token: "LouSessionToken000000010", name: "Lonely Lou" }
]

FIXTURES = USERS.each_with_index.map do |u, i|
  session_row(900001 + i, u[:id], u[:token], "198.51.100.7")
end
FIXTURES.each { |sql| ActiveRecord::Base.connection.execute(sql) }

CSRF = "bTAxU2lkZWJhcnNDc3JmVG9rZW4wMG0wMVNpZGViYXI"
PAGE = "text/html, application/xhtml+xml"
TURBO = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"

def jar
  ActionDispatch::Request.new(Rails.application.env_config.merge("HTTP_HOST" => HOST, "rack.input" => StringIO.new)).cookie_jar
end

def cookie(name, raw) = "#{name}=#{Rack::Utils.escape(raw)}"

def session_token(token)
  cookies = jar
  cookies.signed.permanent[:session_token] = { value: token, httponly: true, same_site: :lax }
  cookie("session_token", cookies[:session_token])
end

def rails_session(data)
  cookies = jar
  cookies.encrypted[:_campfire_session] = { value: data }
  cookie("_campfire_session", cookies[:_campfire_session])
end

def signed_in(token, extra_headers = {})
  session_cookie = session_token(token)
  csrf_cookie = rails_session({ "session_id" => "0123456789abcdef0123456789abcdef", "_csrf_token" => CSRF })
  {
    "Cookie" => "#{session_cookie}; #{csrf_cookie}",
    "Accept" => PAGE,
    "User-Agent" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
  }.merge(extra_headers)
end

CASES = []

# Every seed user gets /users/me/sidebar
USERS.each do |u|
  CASES << [ "sidebar for #{u[:name]}", "GET", "/users/me/sidebar", signed_in(u[:token]), nil ]
end

# Specific /users/:user_id/sidebar
CASES << [ "sidebar for David by explicit id", "GET", "/users/127326141/sidebar", signed_in(USERS[0][:token]), nil ]

# Turbo frame request
CASES << [ "sidebar in Turbo frame for David", "GET", "/users/me/sidebar", signed_in(USERS[0][:token], "Turbo-Frame" => "user_sidebar"), nil ]

# Signed out
CASES << [ "sidebar signed out", "GET", "/users/me/sidebar", {
  "Accept" => PAGE,
  "User-Agent" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
}, nil ]

# JSON format -> 406
CASES << [ "sidebar as JSON", "GET", "/users/me/sidebar.json", signed_in(USERS[0][:token]), nil ]

RESPONSE_HEADERS = %w[ content-type location vary cache-control set-cookie x-frame-options etag ]

results = CASES.map do |name, method, path, headers, body|
  env = Rack::MockRequest.env_for("http://#{HOST}#{path}", method: method, input: body || "")
  env["REMOTE_ADDR"] = "198.51.100.7"
  headers.each { |key, value| env["HTTP_#{key.upcase.tr("-", "_")}"] = value }

  status, response_headers, response_body = Rails.application.call(env)
  text = +""
  if response_body.respond_to?(:to_path)
    text << File.binread(response_body.to_path)
  else
    response_body.each { |chunk| text << chunk }
  end
  response_body.close if response_body.respond_to?(:close)

  response_headers = response_headers.to_h.transform_keys(&:downcase)
  {
    "name" => name,
    "request" => { "method" => method, "path" => path, "headers" => headers, "body" => body },
    "response" => {
      "status" => status,
      "headers" => response_headers.slice(*RESPONSE_HEADERS),
      "body" => text.force_encoding("UTF-8")
    }
  }
end

payload = {
  "now" => NOW.iso8601,
  "fixtures" => FIXTURES,
  "cases" => results
}

out_path = File.join(WORK, OUTPUT)
FileUtils.mkdir_p(File.dirname(out_path))
File.write(out_path, JSON.pretty_generate(payload))
puts "Wrote #{results.length} cases to #{out_path}"
