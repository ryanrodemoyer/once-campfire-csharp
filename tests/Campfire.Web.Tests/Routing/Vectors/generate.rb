# Regenerates routes.json: what the reference's router does beyond vectors/campfire_routes.json,
# which pins the route table and recognition but not route names or URL generation. This draws the
# same routes.rb files with Action Pack from the revisions the reference pins (reference/Gemfile.lock:
# rails/rails 1a02651, turbo-rails 30cd8fc, rack 3.2.6), checks the table equals
# campfire_routes.json, and records every named route's path helper over a spread of arguments,
# the direct routes, PublicExceptions' JSON and XML bodies, and request.formats.
#
#   git clone --filter=blob:none https://github.com/rails/rails && git -C rails checkout 1a02651
#   git clone --filter=blob:none https://github.com/hotwired/turbo-rails && git -C turbo-rails checkout 30cd8fc
#   cat > Gemfile <<GEMFILE
#   source "https://rubygems.org"
#   gem "activesupport", path: "rails/activesupport"
#   gem "actionview", path: "rails/actionview"
#   gem "actionpack", path: "rails/actionpack"
#   gem "rack", "3.2.6"
#   GEMFILE
#   bundle install
#   RAILS_DIR=rails TURBO_DIR=turbo-rails bundle exec ruby \
#     tests/Campfire.Web.Tests/Routing/Vectors/generate.rb > tests/Campfire.Web.Tests/Routing/Vectors/routes.json
Encoding.default_external = Encoding::UTF_8
require "json"
require "action_controller"
require "active_support/core_ext/time"

REPOSITORY = File.expand_path("../../../..", __dir__)
RAILS_DIR = File.expand_path(ENV.fetch("RAILS_DIR"))
TURBO_DIR = File.expand_path(ENV.fetch("TURBO_DIR"))

raise "Rails 8.2.0.alpha expected" unless ActionPack::VERSION::STRING == "8.2.0.alpha"

module Rails
  def self.application = APPLICATION
end
APPLICATION = Struct.new(:routes).new(ActionDispatch::Routing::RouteSet.new)

module ActiveStorage
  def self.routes_prefix = "/rails/active_storage"
  def self.draw_routes = true
end

module Turbo
  def self.draw_routes = true
end

class Current
  class << self
    attr_accessor :account
  end
end

Record = Struct.new(:id, :updated_at, :avatar_token) do
  def to_param = id.to_s
end

# The order bin/rails routes lists them in: the app, then the engines' route files, drawn into one
# set the way Rails::Application::RoutesReloader does.
Rails.application.routes.disable_clear_and_finalize = true
[
  File.join(REPOSITORY, "reference/config/routes.rb"),
  File.join(TURBO_DIR, "config/routes.rb"),
  File.join(RAILS_DIR, "actionmailbox/config/routes.rb"),
  File.join(RAILS_DIR, "activestorage/config/routes.rb")
].each { |file| load file }
Rails.application.routes.disable_clear_and_finalize = false
Rails.application.routes.finalize!

set = Rails.application.routes
routes = set.routes.filter_map do |route|
  next if route.verb.blank? || route.internal
  { name: route.name, verb: route.verb, path: route.path.spec.to_s,
    endpoint: "#{route.defaults[:controller]}##{route.defaults[:action]}",
    defaults: route.defaults.except(:controller, :action).transform_values(&:to_s) }
end

table = JSON.parse(File.read(File.join(REPOSITORY, "vectors/campfire_routes.json"))).fetch("routes")
unless JSON.parse(JSON.generate(routes.map { |route| route.except(:name) })) == table
  raise "the drawn routes differ from vectors/campfire_routes.json"
end

# Like a controller or view: SetCurrentRequest#default_url_options always has the request's host and
# protocol, so Rails never takes its optimized path helpers.
class Helpers
  include ActionDispatch::Routing::UrlFor
  include Rails.application.routes.url_helpers

  def default_url_options = { host: "example.com", protocol: "http" }
end
helpers = Helpers.new

ARGUMENTS = [ 1, "2", "a b", "café", "a/b", "x.y", "100%", "?#[]", "-_.~!$&'()*+,;=:@" ]
GLOBS = [ "photo.jpg", "dir/photo é.tar.gz", "a b/c%d" ]
OPTIONS = [
  {},
  { format: "json" },
  { format: :turbo_stream },
  { q: "a b", page: 2 },
  { before: nil, after: 3 },
  { user_ids: [ 1, 2 ] },
  { user_ids: [] },
  { flag: true, other: false },
  { filter: { b: "x", a: [ "y", nil ] } },
  { "z" => "&=", "a+b" => "c d", "é" => "ü" },
  { anchor: "top section" }
]

def required_names(route)
  route.path.required_names
end

def arguments_for(route, offset)
  required_names(route).each_with_index.map do |name, index|
    if route.path.spec.to_s.include?("*#{name}")
      GLOBS[(offset + index) % GLOBS.size]
    else
      ARGUMENTS[(offset + index) % ARGUMENTS.size]
    end
  end
end

def call(helpers, helper, arguments, options)
  helpers.public_send(helper, *arguments, **options)
rescue ActionController::UrlGenerationError => error
  { error: error.class.name }
end

def jsonable(value)
  case value
  when Hash then value.to_h { |key, entry| [ key.to_s, jsonable(entry) ] }
  when Array then value.map { |entry| jsonable(entry) }
  when Symbol then value.to_s
  else value
  end
end

generations = set.named_routes.names.sort.flat_map do |name|
  route = set.named_routes[name]
  next [] if route.nil?

  cases = ARGUMENTS.size.times.flat_map do |offset|
    arguments = arguments_for(route, offset)
    options = offset.zero? ? OPTIONS : [ OPTIONS[offset % OPTIONS.size] ]
    options.map { |option| [ arguments, option ] }
  end
  cases << [ [], {} ] << [ arguments_for(route, 0).first(1), {} ] << [ [ nil ] * required_names(route).size, {} ]
  cases << [ [ "" ] * required_names(route).size, {} ] << [ arguments_for(route, 0) + [ "json" ], {} ]
  cases << [ [], { format: "json" } ] if route.defaults.key?(:user_id)
  cases << [ arguments_for(route, 0), { format: "json" } ] << [ arguments_for(route, 0), { format: "txt" } ] if route.defaults.key?(:format)

  cases.uniq.map do |arguments, options|
    { helper: "#{name}_path", arguments: jsonable(arguments), options: jsonable(options),
      path: call(helpers, "#{name}_path", arguments, options) }
  end
end

account = Record.new(1, Time.utc(2026, 1, 1, 12, 0, 0, 123456))
user = Record.new(7, Time.utc(2025, 12, 31, 23, 59, 59), "avatar-token--abc")
directs = []
[ nil, account ].each do |current|
  Current.account = current
  [ {}, { size: :small }, { size: "large" } ].each do |options|
    directs << { helper: "fresh_account_logo_path", account_updated_at: current&.updated_at&.iso8601(6),
                 options: jsonable(options), path: helpers.fresh_account_logo_path(**options) }
  end
end
directs << { helper: "fresh_user_avatar_path", user: { avatar_token: user.avatar_token, updated_at: user.updated_at.iso8601(6) },
             options: {}, path: helpers.fresh_user_avatar_path(user) }
directs << { helper: "fresh_user_avatar_url", user: { avatar_token: user.avatar_token, updated_at: user.updated_at.iso8601(6) },
             options: {}, path: helpers.fresh_user_avatar_url(user) }
directs << { helper: "room_url", arguments: [ 1 ], options: {}, path: helpers.room_url(1) }
directs << { helper: "room_url", arguments: [ 1 ], options: { host: "campfire.test", port: 3000, protocol: "https" }, path: helpers.room_url(1, host: "campfire.test", port: 3000, protocol: "https") }

# ShowExceptions + PublicExceptions bodies for the JSON and XML formats.
require "active_support/core_ext/hash/conversions"
require "active_support/json"
public_exceptions = [ 400, 404, 405, 406, 413, 422, 500, 501 ].map do |status|
  body = { status: status, error: Rack::Utils::HTTP_STATUS_CODES.fetch(status, Rack::Utils::HTTP_STATUS_CODES[500]) }
  { status: status, json: body.to_json, xml: body.to_xml }
end

# request.formats, with turbo-rails' type registered as its engine does.
Mime::Type.register "text/vnd.turbo-stream.html", :turbo_stream
ACCEPTS = [
  nil, "", "*/*", "text/html", "application/json", "text/vnd.turbo-stream.html, text/html, application/xhtml+xml",
  "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8", "application/json, text/javascript, */*; q=0.01",
  "image/svg+xml", "image/*", "text/*", "application/*", "text/plain;q=0.5, text/html", "text/xml, application/xml", "text/xml",
  "application/atom+xml, application/xml;q=0.8, text/xml;q=0.9", "application/foo", "application/json;q=0", "text/html;q=", "*/*;q=0.1, application/json"
]
formats = ACCEPTS.product([ nil, "XMLHttpRequest" ], [ "/rooms/1", "/rooms/1.json", "/users/me/avatar.svg" ], [ nil, "turbo_stream", "nope" ], [ nil, "application/json" ]).map do |accept, xhr, path, format, content_type|
  env = Rack::MockRequest.env_for(format ? "#{path}?format=#{format}" : path)
  env["HTTP_ACCEPT"] = accept if accept
  env["HTTP_X_REQUESTED_WITH"] = xhr if xhr
  env["CONTENT_TYPE"] = content_type if content_type
  request = ActionDispatch::Request.new(env)
  { accept: accept, xhr: !xhr.nil?, path: path, format: format, content_type: content_type, formats: request.formats.map(&:to_s) }
end

puts JSON.pretty_generate(routes: routes, generations: generations, directs: directs, public_exceptions: public_exceptions, formats: formats)
