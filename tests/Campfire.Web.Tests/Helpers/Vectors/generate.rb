# Regenerates helpers.json: what the reference's view helpers render, for HelperGoldenTests. Each
# case is an ERB snippet rendered by ActionView, turbo-rails and Propshaft's helper at the revisions
# reference/Gemfile.lock pins (rails/rails 1a02651, turbo-rails 30cd8fc, propshaft e49a9de), with
# the reference's own helper modules from reference/app/helpers and its layout from
# reference/app/views/layouts. The C# side builds the same cases from the same inputs.
#
#   git clone --filter=blob:none https://github.com/rails/rails && git -C rails checkout 1a02651
#   git clone --filter=blob:none https://github.com/hotwired/turbo-rails && git -C turbo-rails checkout 30cd8fc
#   git clone --filter=blob:none https://github.com/rails/propshaft && git -C propshaft checkout e49a9de
#   cat > Gemfile <<GEMFILE
#   source "https://rubygems.org"
#   gem "activesupport", path: "rails/activesupport"
#   gem "actionview", path: "rails/actionview"
#   gem "actionpack", path: "rails/actionpack"
#   gem "activemodel", path: "rails/activemodel"
#   gem "rack", "3.2.6"
#   gem "nokogiri", "1.19.4"
#   gem "rails-html-sanitizer", "1.7.1"
#   gem "loofah", "2.25.2"
#   gem "erubi", "1.13.1"
#   gem "i18n", "1.14.7"
#   gem "builder", "3.3.0"
#   gem "concurrent-ruby", "1.3.7"
#   gem "tzinfo", "2.0.6"
#   gem "rails-dom-testing", "2.3.0"
#   gem "json", "2.21.2"
#   gem "bigdecimal", "3.3.1"
#   gem "minitest", "5.26.2"
#   gem "connection_pool", "2.5.5"
#   gem "globalid", "1.3.0"
#   GEMFILE
#   bundle install
#   RAILS_DIR=rails TURBO_DIR=turbo-rails PROPSHAFT_DIR=propshaft bundle exec ruby \
#     tests/Campfire.Web.Tests/Helpers/Vectors/generate.rb > tests/Campfire.Web.Tests/Helpers/Vectors/helpers.json
Encoding.default_external = Encoding::UTF_8
require "json"
require "action_controller"
require "action_view"
require "action_dispatch/middleware/flash"
require "active_model"
require "global_id"
require "active_support/core_ext/time"

REPOSITORY = File.expand_path("../../../..", __dir__)
RAILS_DIR = File.expand_path(ENV.fetch("RAILS_DIR"))
TURBO_DIR = File.expand_path(ENV.fetch("TURBO_DIR"))
PROPSHAFT_DIR = File.expand_path(ENV.fetch("PROPSHAFT_DIR"))
ASSET_FIXTURES = File.join(REPOSITORY, "reference-rust/crates/assets/tests/reference")

raise "Rails 8.2.0.alpha expected" unless ActionPack::VERSION::STRING == "8.2.0.alpha"

# What config.load_defaults 8.2 sets for Action View (railties' Configuration#load_defaults).
ActionView::Helpers::FormHelper.form_with_generates_ids = true
ActionView::Helpers::FormHelper.form_with_generates_remote_forms = false
ActionView::Helpers::FormTagHelper.default_enforce_utf8 = false
ActionView::Helpers::FormTagHelper.embed_authenticity_token_in_remote_forms = false
ActionView::Helpers::UrlHelper.button_to_generates_button_tag = true
ActionView::Helpers::AssetTagHelper.preload_links_header = true
ActionView::Helpers::AssetTagHelper.apply_stylesheet_media_default = false
ActionView::Base.remove_hidden_field_autocomplete = true
ActionView::Helpers::FormHelper.multiple_file_field_include_hidden = true

SECRET_KEY_BASE = "c20a0c5bc9bbba6b6c4d3e9b8e1ed7b4d3e1c1a9c8a5d1e0f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6"

module Rails
  def self.application = APPLICATION
  def self.configuration = APPLICATION.config
end
APPLICATION = Struct.new(:routes, :config, :assets).new(
  ActionDispatch::Routing::RouteSet.new,
  ActiveSupport::OrderedOptions.new.tap { |config| config.x = ActiveSupport::OrderedOptions.new.tap { |x| x.vapid = ActiveSupport::OrderedOptions.new } }
)

module ActiveStorage
  def self.routes_prefix = "/rails/active_storage"
  def self.draw_routes = true
end

require File.join(REPOSITORY, "reference/lib/rails_ext/string") # String#all_emoji?
require File.join(REPOSITORY, "reference/config/initializers/time_formats") # to_fs(:epoch)

# Action Cable's default mount path; the reference doesn't change it.
module ActionCable
  def self.server = Struct.new(:config).new(Struct.new(:mount_path).new("/cable"))
end

class Current
  class << self
    attr_accessor :user, :account
  end
end

GlobalID.app = "campfire"

# turbo-rails without its engine: the helpers, the tag builder and the stream name signing, with the
# key the engine derives from the app's key generator.
$LOAD_PATH.unshift(File.join(TURBO_DIR, "lib"))
$LOADED_FEATURES << File.join(TURBO_DIR, "lib/turbo/engine.rb") # the engine needs railties
require "turbo-rails"
module Turbo::Streams; end
%w[
  app/channels/turbo/streams/stream_name.rb
  app/helpers/turbo/frames_helper.rb
  app/helpers/turbo/streams/action_helper.rb
  app/helpers/turbo/streams_helper.rb
  app/models/turbo/streams/tag_builder.rb
].each { |file| require File.join(TURBO_DIR, file) }
module Turbo
  class StreamsChannel
    extend Turbo::Streams::StreamName
  end
end
KEY_GENERATOR = ActiveSupport::KeyGenerator.new(SECRET_KEY_BASE, iterations: 1000, hash_digest_class: OpenSSL::Digest::SHA256)
Turbo.signed_stream_verifier_key = KEY_GENERATOR.generate_key("turbo/signed_stream_verifier_key")

# Propshaft's helper over the reference's precompiled manifest.
module Propshaft
  class MissingAssetError < StandardError; end
end
require File.join(PROPSHAFT_DIR, "lib/propshaft/helper")
MANIFEST = JSON.parse(File.read(File.join(ASSET_FIXTURES, "manifest.json")))
ASSETS = Struct.new(:resolver, :load_path).new(
  Object.new.tap { |resolver| def resolver.resolve(path) = (entry = MANIFEST[path]) && "/assets/#{entry.fetch("digested_path")}" },
  Object.new.tap { |load_path| def load_path.asset_paths_by_type(type) = MANIFEST.keys.select { |path| File.extname(path) == ".#{type}" }.sort }
)
APPLICATION.assets = ASSETS

Rails.application.routes.disable_clear_and_finalize = true
[
  File.join(REPOSITORY, "reference/config/routes.rb"),
  File.join(TURBO_DIR, "config/routes.rb"),
  File.join(RAILS_DIR, "actionmailbox/config/routes.rb"),
  File.join(RAILS_DIR, "activestorage/config/routes.rb")
].each { |file| load file }
Rails.application.routes.disable_clear_and_finalize = false
Rails.application.routes.finalize!

# Records: Active Model naming, conversion and GlobalID, with attributes the form fields read.
class FakeRecord
  extend ActiveModel::Naming
  include ActiveModel::Conversion
  include GlobalID::Identification

  attr_reader :id

  def initialize(id = nil, **attributes)
    @id = id
    @attributes = attributes
  end

  def persisted? = !id.nil?

  def respond_to_missing?(name, include_private = false) = @attributes.key?(name) || super

  def method_missing(name, *args)
    @attributes.key?(name) ? @attributes[name] : super
  end
end

module Rooms
  class Open < FakeRecord
    def direct? = false
  end
  class Closed < FakeRecord
    def direct? = false
  end
  class Direct < FakeRecord
    def direct? = true
  end
end
class User < FakeRecord; end
class Account < FakeRecord; end
class Message < FakeRecord
  def to_key = [ client_message_id ]
end
class Boost < FakeRecord; end

# The app's helpers, minus what needs Action Text or models (message presentation, editable_body).
module ContentFilters
  EDITOR_FORMATTING_TAGS = %w[ s u mark table thead tbody tfoot tr th td ]
  EDITOR_FORMATTING_ATTRIBUTES = %w[ data-language ]
end
module Users; end
%w[
  accounts_helper application_helper broadcasts_helper cable_helper clipboard_helper drop_target_helper
  emoji_helper forms_helper messages_helper qr_code_helper rich_text_helper rooms_helper searches_helper
  time_helper translations_helper users_helper version_helper rooms/involvements_helper users/avatars_helper
  users/filter_helper users/profiles_helper users/sidebar_helper
].each { |file| require File.join(REPOSITORY, "reference/app/helpers", file) }

Rails.application.config.app_version = "0"
Rails.application.config.x.vapid.public_key = nil

class GoldenController < ActionController::Base
  include Rails.application.routes.url_helpers

  def self.controller_path = "sessions/transfers"
end

VIEW_HELPERS = [
  Propshaft::Helper, Turbo::FramesHelper, Turbo::StreamsHelper, Rails.application.routes.url_helpers,
  AccountsHelper, ApplicationHelper, BroadcastsHelper, CableHelper, ClipboardHelper, DropTargetHelper,
  EmojiHelper, FormsHelper, MessagesHelper, QrCodeHelper, RichTextHelper, RoomsHelper, SearchesHelper,
  TimeHelper, TranslationsHelper, UsersHelper, VersionHelper, Rooms::InvolvementsHelper,
  Users::AvatarsHelper, Users::FilterHelper, Users::ProfilesHelper, Users::SidebarHelper
]

VIEW_CLASS = ActionView::Base.with_empty_template_cache.tap do |view_class|
  VIEW_HELPERS.each { |helper| view_class.include(helper) }
  view_class.class_eval do
    # The CSRF token is random; the C# side gets the same stand-in, so the action and method each
    # form asks its token for are checked too.
    def protect_against_forgery? = true
    def form_authenticity_token(form_options: {}) = "token(#{form_options[:action]}|#{form_options[:method]})"
    # What the reference app's own javascript_importmap_tags rendered (F04's fixture).
    def javascript_importmap_tags = File.read(File.join(ASSET_FIXTURES, "javascript_importmap_tags.html")).html_safe
    # ContentSecurityPolicy's helper method: the reference configures no policy.
    def content_security_policy? = false
    def last_room_visited = @last_room_visited
    def room_display_name(room, for_user: nil) = room.name
  end
end

# The request every case renders in: GET https://campfire.test/session/transfers/abc.
def build_view(referrer: nil)
  env = Rack::MockRequest.env_for("https://campfire.test/session/transfers/abc")
  env["HTTP_REFERER"] = referrer if referrer
  env["rack.session"] = {}
  request = ActionDispatch::Request.new(env)
  request.path_parameters = { controller: "sessions/transfers", action: "show", id: "abc" }
  controller = GoldenController.new
  controller.set_request!(request)
  controller.set_response!(ActionDispatch::Response.new)
  lookup = ActionView::LookupContext.new([ File.join(REPOSITORY, "reference/app/views") ])
  view = VIEW_CLASS.new(lookup, {}, controller)
  [ view, controller ]
end

TIME = Time.utc(2026, 9, 26, 12, 23, 46, Rational(483_521))
LATER = Time.utc(2026, 9, 27, 8, 1, 2)

ROOM = Rooms::Open.new(7, name: "Lobby <1>", updated_at: TIME)
DIRECT = Rooms::Direct.new(9, name: "Ping", updated_at: TIME)
USER = User.new(3, name: "Kevin & Co", email_address: "kevin@example.com", bio: "Hi <there>", role: "administrator",
  title: "Kevin & Co – Hi <there>", avatar_token: "eyJfcmFpbHMiOnsiZGF0YSI6M319--abc", updated_at: TIME)
ACCOUNT = Account.new(1, name: "37s", settings: Struct.new(:restrict_room_creation_to_administrators).new(true), updated_at: LATER)
MESSAGE = Message.new(11, client_message_id: "a1b2-c3", creator_id: 3, created_at: TIME, updated_at: LATER)
NEW_MESSAGE = Message.new(nil, client_message_id: nil)

CASES = {
  # TagHelper
  "tag_div_text" => %q(<%= tag.div "a & <b>" %>),
  "tag_div_nil" => %q(<%= tag.div %>),
  "tag_div_safe" => %q(<%= tag.div "<b>bold</b>".html_safe, id: "x" %>),
  "tag_class_array" => %q(<%= tag.span "x", class: [ "a", nil, "b", { c: true, d: false } ] %>),
  "tag_data_values" => %q(<%= tag.div data: { a: "s", b: 1, c: true, d: false, e: nil, f: { x: 1, y: "<>" }, g: [ 1, "a" ], h: %(<>&"'), i_j: "v", k: "->".html_safe, l: 1.5 } %>),
  "tag_aria_values" => %q(<%= tag.div aria: { hidden: true, label: "Close <x>", describedby: [ "a", "b" ], empty: [], flags: { g: true, h: false }, n: nil } %>),
  "tag_boolean_attributes" => %q(<%= tag.input type: "checkbox", hidden: true, disabled: false, checked: "checked", required: nil, autofocus: "" %>),
  "tag_attribute_escaping" => %q(<%= tag.a "x", title: %(a"b'<c>&), href: "/?a=1&b=2", data: { safe: "&amp;\"".html_safe } %>),
  "tag_numbers" => %q(<%= tag.summary "s", tabindex: -1, width: 1.5, height: 10 %>),
  "tag_key_escaping" => %q(<%= tag.div "x", "bad key" => "v", "1st" => "w", "ok:name" => "z" %>),
  "tag_unescaped" => %q(<%= tag.div "<b>", title: "<i>", escape: false %>),
  "tag_void_elements" => %q(<%= tag.meta name: "a", content: "b" %><%= tag.link rel: "icon", href: "/x" %><%= tag.input type: "text" %><%= tag.img src: "/a.png" %><%= tag.br %>),
  "tag_dasherized" => %q(<%= tag.turbo_frame id: "f" %><%= tag.lexxy_prompt "p", trigger: "@" %>),
  "tag_textarea" => %q(<%= tag.textarea "a\nb", name: "t" %>),
  "tag_block" => %q(<%= tag.div class: "outer" do %><p>inner <%= "<x>" %></p><% end %>),
  "tag_legacy" => %q(<%= tag(:meta, name: "a", content: "b") %>|<%= tag("input", { type: "text" }, true) %>|<%= tag(:br) %>),
  "content_tag" => %q(<%= content_tag(:p, "a<b", class: "y") %><%= content_tag(:section, id: "s") do %>in<% end %>),
  "token_list" => %q(<%= token_list("a b", "a", nil, { c: true, d: false }, [ "e", [ "f" ] ]) %>),
  "safe_join" => %q(<%= safe_join([ "<a>", "<b>".html_safe, [ "c", nil ] ], "<br>") %>|<%= safe_join([ "x", "y" ], "<hr>".html_safe) %>),
  "raw" => %q(<%= raw "<b>" %>),

  # CaptureHelper
  "content_for" => %q(<% content_for :x, "a<b" %><% content_for :x do %><b>c</b><% end %><% content_for :x, "<i>".html_safe %><%= content_for(:x) %>|<%= content_for?(:x) %>|<%= content_for?(:y) %>|<%= content_for(:y).inspect %>),
  "content_for_flush" => %q(<% content_for :x, "a" %><% content_for :x, "b", flush: true %><%= content_for(:x) %>),

  # RecordIdentifier
  "dom_id" => %q(<%= dom_id(ROOM) %>|<%= dom_id(ROOM, :messages) %>|<%= dom_id(NEW_MESSAGE) %>|<%= dom_id(NEW_MESSAGE, :form) %>|<%= dom_id(MESSAGE) %>|<%= dom_id(USER, :role) %>|<%= dom_class(DIRECT) %>|<%= dom_class(ROOM, :edit) %>),

  # UrlHelper
  "link_to_text" => %q(<%= link_to "Home & away", "/" %>),
  "link_to_nil_name" => %q(<%= link_to nil, "/x?a=1&b=2" %>),
  "link_to_options" => %q(<%= link_to "<b>", "/r", class: "c", title: "t", data: { turbo_frame: "_top" } %>),
  "link_to_method" => %q(<%= link_to "Delete", "/r/1", method: :delete %>|<%= link_to "Get", "/r", method: :get, rel: "me" %>|<%= link_to "Put", "/r", method: :put, rel: "me" %>|<%= link_to "Already", "/r", method: :post, rel: "nofollow" %>),
  "link_to_remote" => %q(<%= link_to "R", "/r", remote: true %>),
  "link_to_href_override" => %q(<%= link_to "H", "/r", href: "#top" %>),
  "link_to_block" => %q(<%= link_to "/rooms/1", class: "btn", id: "go" do %><span>Go</span><% end %>),
  "button_to_text" => %q(<%= button_to "Delete", "/rooms/1", method: :delete, class: "btn" %>),
  "button_to_block" => %q(<%= button_to "/rooms/1/involvement", method: :put, class: "btn", aria: { label: "Change" }, data: { turbo_confirm: "Sure?" } do %><img src="/x.svg"><% end %>),
  "button_to_post" => %q(<%= button_to "Go", "/go" %>|<%= button_to "Post", "/go", method: :post %>|<%= button_to "Patch", "/go", method: :patch %>),
  "button_to_get" => %q(<%= button_to "Search", "/search", method: :get %>),
  "button_to_form_options" => %q(<%= button_to "A", "/a", form: { class: "f", data: { x: 1 } } %>|<%= button_to "B", "/b", form_class: "fc" %>),
  "button_to_params" => %q(<%= button_to "P", "/p", params: { b: 2, a: [ 1, "x" ], c: { d: "e" } } %>),
  "button_to_token_options" => %q(<%= button_to "N", "/n", authenticity_token: false %>|<%= button_to "S", "/s", authenticity_token: "given" %>),
  "button_to_remote" => %q(<%= button_to "R", "/r", remote: true %>),
  "mail_to" => %q(<%= mail_to "kevin@example.com" %>|<%= mail_to "a+b@x.com", "Write <me>", class: "c" %>|<%= mail_to "a@x.com", nil, subject: "Hi there & bye", body: "x y", cc: "c@x.com" %>),

  # AssetTagHelper and friends
  "image_tag" => %q(<%= image_tag "check.svg" %>|<%= image_tag "check.svg", aria: { hidden: "true" }, size: 20 %>|<%= image_tag "campfire-icon.png", alt: "Campfire logo", width: 256, height: 216 %>|<%= image_tag "remove.svg", size: "20x30", class: "x" %>|<%= image_tag "external/switch.svg", alt: "the switch", size: 22 %>),
  "image_tag_paths" => %q(<%= image_tag "/rails/active_storage/x.png", loading: "lazy" %>|<%= image_tag "https://example.com/a.png" %>|<%= image_tag "check.svg?v=1#f" %>|<%= image_tag "check.svg", size: "big" %>),
  "image_path" => %q(<%= image_path("add.svg") %>|<%= image_url("screenshots/android-chat.png") %>|<%= asset_path("check.svg") %>|<%= image_path("") %>),
  "stylesheet_link_tag_all" => %q(<%= stylesheet_link_tag :all, "data-turbo-track": "reload" %>),
  "csrf_meta_tags" => %q(<%= csrf_meta_tags %>),

  # FormTagHelper
  "hidden_field_tag" => %q(<%= hidden_field_tag :push_subscription_endpoint, nil, data: { sessions_target: "pushSubscriptionEndpoint" } %>|<%= hidden_field_tag "user_ids[]", 5, id: nil %>|<%= hidden_field_tag "boost[content]", "👍" %>),
  "check_box_tag" => %q(<%= check_box_tag "user_ids[]", 5, true, class: "switch__input", id: nil %>|<%= check_box_tag "user_ids[]", 6, false, class: "switch__input", id: nil %>|<%= check_box_tag "agree" %>),
  "text_field_tag" => %q(<%= text_field_tag "q", "a&b", class: "i" %>),
  "button_tag" => %q(<%= button_tag "Go" %>|<%= button_tag class: "btn", type: "submit" do %><i>x</i><% end %>|<%= button_tag type: "submit", form: "f", name: "n" do %>y<% end %>),

  # FormHelper
  "form_with_url_block" => %q(<%= form_with url: "/session", method: :delete, data: { controller: "sessions" } do %><%= hidden_field_tag :push_subscription_endpoint, nil %><% end %>),
  "form_with_model" => %q(<%= form_with model: USER, url: "/users/3", class: "center" do |form| %>
<%= form.text_field :name, class: "input", autocomplete: "name", placeholder: "Name", autofocus: true, required: true, maxlength: 20 %>
<%= form.email_field :email_address, class: "input", value: "other@example.com" %>
<%= form.password_field :password, class: "input", required: true, maxlength: 72 %>
<%= form.text_area :bio, class: "input", maxlength: 200 %>
<%= form.text_area :bio, size: "20x3", value: "x" %>
<%= form.url_field :webhook_url, class: "input" %>
<%= form.hidden_field :id %>
<%= form.check_box :role, { data: { action: "form#submit" }, hidden: true, id: "role_3", disabled: false }, "administrator", "member" %>
<%= form.check_box :role, {}, "member", "administrator" %>
<%= form.text_field :name, name: "room[name]", id: "room_name" %>
<%= form.text_field :missing, id: nil %>
<%= form.button class: "btn", type: "submit" do %>Save<% end %>
<%= form.button "Plain" %>
<%= form.button %>
<% end %>),
  "form_with_file_field" => %q(<%= form_with model: USER, url: "/users/3/profile", method: :patch, data: { controller: "form" } do |form| %><%= form.file_field :avatar, id: "file", class: "input", accept: "image/*" %><%= form.file_field :photos, multiple: true %><% end %>),
  "form_with_new_model" => %q(<%= form_with model: NEW_MESSAGE, url: "/rooms/7/messages", id: "composer", data: { controller: "composer" } do |form| %><%= form.hidden_field :client_message_id, data: { composer_target: "clientid" } %><%= form.button name: "send", type: "submit", data: { action: "composer#submit" } do %>Send<% end %><%= form.button %><% end %>),
  "form_with_fields_for" => %q(<%= form_with model: ACCOUNT, url: "/account", method: :put, data: { controller: "form" }, class: "flex" do |form| %><%= form.fields_for :settings, ACCOUNT.settings do |settings_form| %><%= settings_form.hidden_field :restrict_room_creation_to_administrators, value: !ACCOUNT.settings.restrict_room_creation_to_administrators %><% end %><% end %>),
  "form_with_scope" => %q(<%= form_with url: "/searches", scope: :search, class: "s", html: { role: "search" } do |form| %><%= form.text_field :q, value: "a b", role: "searchbox", aria: { label: "search" } %><% end %>),
  "form_with_get" => %q(<%= form_with url: "/searches", method: :get do |form| %><%= form.text_field :q %><% end %>),
  "form_with_no_block" => %q(<%= form_with url: "/rooms/7/messages/11", method: :delete, id: "delete_form", data: { turbo_frame: "edit" } %>),
  "form_with_current_url" => %q(<%= form_with method: :put %>),
  "form_with_options" => %q(<%= form_with url: "/x", authenticity_token: false, local: false, multipart: true, skip_enforcing_utf8: false do %>x<% end %>|<%= form_with url: "/y", authenticity_token: "given", aria: { label: "dropped" } do %>y<% end %>),
  "form_with_persisted_method_override" => %q(<%= form_with model: ROOM, url: "/rooms/opens/7", method: :post do |form| %><%= form.text_field :name %><% end %>),

  # turbo-rails
  "turbo_frame_tag" => %q(<%= turbo_frame_tag "x" %>|<%= turbo_frame_tag :next_page_container, loading: :lazy, src: "/accounts/users?page=2", target: "_top" %>|<%= turbo_frame_tag "f", src: "" %>),
  "turbo_frame_tag_record" => %q(<%= turbo_frame_tag MESSAGE, :boosting do %>b<% end %>|<%= turbo_frame_tag dom_id(ROOM, :involvement), id: "ignored", data: { controller: "turbo-frame" } do %>i<% end %>),
  "turbo_stream_from" => %q(<%= turbo_stream_from :rooms %>|<%= turbo_stream_from USER, :rooms %>|<%= turbo_stream_from ROOM, :messages, channel: "RoomMessagesChannel" %>|<%= turbo_stream_from "a", nil, data: { x: 1 } %>),
  "turbo_stream_actions" => %q(<%= turbo_stream.append "messages", "<b>raw</b>" %>|<%= turbo_stream.prepend "m", "<i>".html_safe %>|<%= turbo_stream.remove "message_a1" %>|<%= turbo_stream.update "u", "x", method: :morph %>|<%= turbo_stream.replace "r", "y" %>|<%= turbo_stream.before "b", "1" %>|<%= turbo_stream.after "a", "2" %>),
  "turbo_stream_block" => %q(<%= turbo_stream.append dom_id(ROOM, :messages) do %><div>new</div><% end %><%= turbo_stream.replace "r", method: :morph do %>r<% end %>),
  "turbo_stream_record_target" => %q(<%= turbo_stream.remove MESSAGE %>),

  # The app's helpers
  "page_title_tag" => %q(<%= page_title_tag %>|<% @page_title = "Rooms & <more>" %><%= page_title_tag %>),
  "current_user_meta_tags" => %q(<%= current_user_meta_tags.inspect %>|<% Current.user = USER %><%= current_user_meta_tags %><% Current.user = nil %>),
  "custom_styles_tag" => %q(<%= custom_styles_tag.inspect %>|<% Current.account = Account.new(1, custom_styles: "body { color: red } </style><x>") %><%= custom_styles_tag %>|<% Current.account = Account.new(1, custom_styles: "") %><%= custom_styles_tag %><% Current.account = nil %>),
  "body_classes" => %q([<%= body_classes %>]|<% @body_class = "sidebar" %><% Current.user = User.new(1, can_administer?: true) %><% Current.account = Account.new(1, logo: Struct.new(:attached?).new(true)) %>[<%= body_classes %>]<% Current.user = User.new(2, can_administer?: false) %><% Current.account = Account.new(1, logo: Struct.new(:attached?).new(false)) %>[<%= body_classes %>]<% Current.user = Current.account = nil %>),
  "link_back_to" => %q(<%= link_back_to "/rooms/7" %>),
  "account_logo_tag" => %q(<% Current.account = ACCOUNT %><%= account_logo_tag %>|<%= account_logo_tag(style: "avatar--large") %><% Current.account = nil %>|<%= account_logo_tag %>),
  "script_aware_action_cable_meta_tag" => %q(<%= script_aware_action_cable_meta_tag %>),
  "version_badge" => %q(<%= version_badge %>),
  "broadcast_image_tag" => %q(<%= broadcast_image_tag "common-file-text.svg", size: 22, class: "colorize--black", aria: { hidden: "true" } %>|<%= broadcast_image_tag "/rails/active_storage/representations/x.png", width: 10, height: 5, class: "message__attachment", loading: "lazy" %>),
  "button_to_copy_to_clipboard" => %q(<%= button_to_copy_to_clipboard "https://campfire.test/join/a&b" do %>Copy<% end %>),
  "drop_target_actions" => %q(<%= drop_target_actions %>),
  "auto_submit_form_with" => %q(<%= auto_submit_form_with method: :put %>|<%= auto_submit_form_with url: "/x", data: { controller: "other", y: 1 } do %>x<% end %>),
  "link_to_zoom_qr_code" => %q(<%= link_to_zoom_qr_code "https://campfire.test/join/abc?x=1" do %>Zoom<% end %>),
  "rich_text_data_actions" => %q(<%= rich_text_data_actions %>),
  "mention_prompt_tag" => %q(<%= mention_prompt_tag ROOM %>),
  "search_results_tag" => %q(<%= search_results_tag do %>r<% end %>),
  "local_datetime_tag" => %q(<%= local_datetime_tag TIME %>|<%= local_datetime_tag TIME, style: :datetime, class: "x" %>|<%= message_timestamp MESSAGE, class: "message__timestamp" %>),
  "translations_for" => %q(<%= translations_for(:email_address) %>),
  "translation_button" => %q(<%= translation_button(:invite_message) %>),
  "avatar_tag" => %q(<%= avatar_tag USER %>|<%= avatar_tag USER, size: 20, class: "avatar--small", loading: "lazy" %>),
  "button_to_direct_room_with" => %q(<%= button_to_direct_room_with USER %>),
  "user_filter_menu_tag" => %q(<%= user_filter_menu_tag do %>m<% end %>),
  "user_filter_search_tag" => %q(<%= user_filter_search_tag %>),
  "profile_form_with" => %q(<% @user = USER %><%= profile_form_with USER, class: "txt-medium" do |form| %><%= form.text_field :name %><% end %>),
  "profile_form_submit_button" => %q(<%= profile_form_submit_button %>),
  "web_share_session_button" => %q(<%= web_share_session_button "https://campfire.test/session/transfers/x", "Link & go", "Text <t>" do %>Share<% end %>),
  "sidebar_turbo_frame_tag" => %q(<%= sidebar_turbo_frame_tag(src: "/users/me/sidebar") %>|<%= sidebar_turbo_frame_tag do %>s<% end %>),
  "link_to_room" => %q(<%= link_to_room ROOM do %>r<% end %>|<%= link_to_room DIRECT, class: [ "direct", "unread": true ], id: "list_rooms_direct_9", data: { extra: "x", room_id: 99 } do %>d<% end %>),
  "link_to_edit_room" => %q(<% @room = ROOM %><%= link_to_edit_room ROOM do %>e<% end %>),
  "link_back_to_last_room_visited" => %q(<%= link_back_to_last_room_visited %>|<% @last_room_visited = ROOM %><%= link_back_to_last_room_visited %>),
  "button_to_delete_room" => %q(<%= button_to_delete_room ROOM %>|<%= button_to_delete_room DIRECT, url: "/rooms/directs/9" %>),
  "button_to_jump_to_newest_message" => %q(<%= button_to_jump_to_newest_message %>),
  "submit_room_button_tag" => %q(<%= submit_room_button_tag %>),
  "composer_form_tag" => %q(<%= composer_form_tag ROOM do |form| %><%= form.hidden_field :client_message_id %><% end %>),
  "turbo_frame_for_involvement_tag" => %q(<%= turbo_frame_for_involvement_tag ROOM do %>i<% end %>),
  "button_to_change_involvement" => %q(<%= button_to_change_involvement ROOM, "mentions" %>|<%= button_to_change_involvement ROOM, "invisible" %>|<%= button_to_change_involvement DIRECT, "everything" %>|<%= button_to_change_involvement DIRECT, "nothing" %>),
  "message_area_tag" => %q(<%= message_area_tag ROOM do %>a<% end %>),
  "messages_tag" => %q(<%= messages_tag ROOM do %>m<% end %>),
  "message_tag" => %q(<% MESSAGE.define_singleton_method(:plain_text_body) { "hi" } %><%= message_tag MESSAGE do %>m<% end %>|<% MESSAGE.define_singleton_method(:plain_text_body) { "👍👍" } %><%= message_tag MESSAGE do %>e<% end %>),
}

# The application layout around an empty page, as it renders for a signed-out visitor, for a
# signed-in administrator with everything the layout reads set, and with an alert.
LAYOUTS = {
  "layout_signed_out" => -> view { },
  "layout_signed_in" => lambda do |view|
    Current.user = User.new(3, name: "Kevin <K>", can_administer?: true)
    Current.account = Account.new(1, custom_styles: ":root { --x: 1 }", logo: Struct.new(:attached?).new(true), updated_at: LATER)
    view.instance_variable_set(:@page_title, "Lobby & more")
    view.instance_variable_set(:@body_class, "sidebar")
    view.flash[:notice] = "Saved <ok>"
    view.content_for(:head, "<meta name=\"x\">".html_safe)
    view.content_for(:nav, "<b>nav</b>".html_safe)
    view.content_for(:footer, "foot & er")
    view.content_for(:sidebar, "<aside-content>".html_safe)
  end,
  "layout_alert" => lambda do |view|
    view.flash[:notice] = "Ignored"
    view.flash[:alert] = "Failed!"
  end,
  "layout_vapid" => lambda do |view|
    Rails.application.config.x.vapid.public_key = "BKey-_123="
    Rails.application.config.app_version = "abc123"
  end
}

def reset_globals
  Current.user = Current.account = nil
  Rails.application.config.x.vapid.public_key = nil
  Rails.application.config.app_version = "0"
end

output = { "cases" => {}, "layouts" => {} }

CASES.each do |name, erb|
  reset_globals
  view, = build_view
  output["cases"][name] = view.render(inline: erb).to_str
end

reset_globals
view, = build_view(referrer: "https://campfire.test/rooms/1")
output["cases"]["link_back_referrer"] = view.render(inline: %q(<%= link_back %>)).to_str
view, = build_view(referrer: "https://campfire.test/session/transfers/abc")
output["cases"]["link_back_same_page"] = view.render(inline: %q(<%= link_back %>)).to_str
view, = build_view
output["cases"]["link_back_no_referrer"] = view.render(inline: %q(<%= link_back %>)).to_str

LAYOUTS.each do |name, setup|
  reset_globals
  view, controller = build_view
  setup.(view)
  html = view.render(inline: "", layout: "layouts/application")
  output["layouts"][name] = { "html" => html.to_str, "link" => controller.response.headers["link"] }
end
reset_globals

# The data the helpers carry, compared as data.
output["reactions"] = EmojiHelper::REACTIONS.to_a
output["translations"] = TranslationsHelper::TRANSLATIONS.to_h { |key, values| [ key.to_s, values.to_a.map { |flag, text| [ flag.to_s, text ] } ] }
output["avatar_colors"] = (1..40).to_h { |id| [ id.to_s, Object.new.extend(Users::AvatarsHelper).avatar_background_color(User.new(id)) ] }

# Sanity: the stylesheet tags are what the reference app itself rendered.
unless output["cases"]["stylesheet_link_tag_all"] == File.read(File.join(ASSET_FIXTURES, "stylesheet_link_tag_all.html")).chomp
  raise "stylesheet_link_tag :all differs from the reference app's"
end

puts JSON.pretty_generate(output)
