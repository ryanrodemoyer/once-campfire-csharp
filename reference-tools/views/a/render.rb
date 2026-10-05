# Renders views agent A's templates through the reference app's real controllers and writes the
# responses as golden files for crates/views/tests/parity_a.rs, plus facts.json with the derived
# values (ids, signed avatar paths, transfer ids, asset digests) the Rust side needs to build its
# view-models.
#
# Run inside the reference app, against freshly loaded fixtures, in production mode:
#
#   DISABLE_SSL=1 DISABLE_DATABASE_ENVIRONMENT_CHECK=1 RAILS_ENV=production bin/rails db:fixtures:load
#   DISABLE_SSL=1 RAILS_ENV=production bin/rails runner reference-tools/views/a/render.rb OUT_DIR
#
# Requests run in-process through ActionDispatch::Integration::Session, so every page goes
# through its controller, before_actions, layout and helpers exactly as it does in production.

require "json"

OUT = ARGV.fetch(0)
FileUtils.mkdir_p(OUT)
Rails.logger.level = :warn
ActiveRecord::Base.logger = nil

HOST = "campfire.test"
CHROME_MAC = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
USER_AGENTS = {
  "chrome_mac" => CHROME_MAC,
  "chrome_windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "safari_mac" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
  "safari_ios" => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
  "chrome_android" => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
  "firefox_mac" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:131.0) Gecko/20100101 Firefox/131.0",
  "firefox_android" => "Mozilla/5.0 (Android 14; Mobile; rv:131.0) Gecko/131.0 Firefox/131.0",
  "edge_windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
  "old_chrome" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36",
  "apple_messages" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_11_1) AppleWebKit/601.2.4 (KHTML, like Gecko) Version/9.0.1 Safari/601.2.4 facebookexternalhit/1.1 Facebot Twitterbot/1.0"
}

# No Redis here: broadcasts go nowhere and remote connections have nothing to close.
ActionCable.server.config.cable = { "adapter" => "async" }
ActionCable.server.restart
ActiveJob::Base.queue_adapter = :test
User.prepend(Module.new { private def close_remote_connections(**) = nil })

# Transfer ids embed an expiry to the second; keep one per user for the whole run.
module StableTransferId
  CACHE = {}
  def transfer_id = CACHE[id] ||= super
end
User.prepend(StableTransferId)

helpers = Rails.application.routes.url_helpers

# Fixtures don't cover Active Storage; start without leftovers from earlier runs.
ActiveStorage::Attachment.delete_all
ActiveStorage::VariantRecord.delete_all
ActiveStorage::Blob.delete_all

# Extra users the fixtures don't have.
User.create!(name: "Ex Employee", email_address: "ex@37signals.com", password: "secret123456").tap(&:deactivate)
User.create!(name: "Spam Ham", email_address: "spam@example.com", password: "secret123456", status: :banned)
User.create!(name: "Anna Bea Cole", email_address: "abc@example.com", password: "secret123456")
User.find_by(name: "Bender Bot").create_webhook!(url: "https://example.com/webhook?a=1&b=2")

def user_facts(user, helpers)
  {
    id: user.id, name: user.name, bio: user.bio, email_address: user.email_address,
    role: user.role, status: user.status,
    avatar_path: helpers.user_avatar_path(user.avatar_token, v: user.updated_at.to_fs(:number)),
    transfer_id: user.transfer_id, bot_key: (user.bot_key if user.bot?), webhook_url: user.webhook_url,
    initials: user.initials, avatar_attached: user.avatar.attached?,
    original_room_id: user.rooms.original&.id, attachable_sgid: user.attachable_sgid, updated_at_number: user.updated_at.to_fs(:number)
  }
end

def account_facts
  account = Account.first
  return nil unless account
  logo = ->(size = nil) { Rails.application.routes.url_helpers.account_logo_path(v: account.updated_at.to_fs(:number), size: size) }
  {
    id: account.id, name: account.name, join_code: account.join_code, custom_styles: account.custom_styles,
    logo_path: logo.(), logo_path_small: logo.(:small), has_logo: account.logo.attached?,
    restrict_room_creation_to_administrators: account.settings.restrict_room_creation_to_administrators?
  }
end

$cases = {}

# The view-model data each controller hands its view, gathered the controller's way so the Rust
# test doesn't have to re-implement the queries.
def view_data(email)
  user = User.find_by(email_address: email)
  return {} unless user
  view = ApplicationController.helpers
  direct, shared = user.memberships.with_ordered_room.partition { |m| m.room.direct? }
  profile_membership = ->(m) {
    { room_id: m.room_id, param_key: m.room.model_name.param_key, involvement: m.involvement, direct: m.room.direct?,
      display_name: view.room_display_name(m.room, for_user: user) }
  }

  # Users::SidebarsController#show
  all_memberships = user.memberships.visible.with_ordered_room
  direct_memberships = all_memberships.select { |m| m.room.direct? }.sort_by { |m| m.room.updated_at }.reverse
  other_memberships = all_memberships.without(direct_memberships)
  exclude_user_ids = Membership.where(room_id: user.rooms.directs.pluck(:id)).pluck(:user_id).uniq.including(user.id)
  placeholders = User.active.where.not(id: exclude_user_ids).order(:created_at).limit([ 20 - exclude_user_ids.count, 0 ].max)

  {
    profile: { shared: shared.map(&profile_membership), direct: direct.map(&profile_membership) },
    sidebar: {
      direct: direct_memberships.map { |m|
        members = m.room.users.without(m.user).presence || [ m.user ]
        { room_id: m.room_id, unread: m.unread?, updated_at_epoch: m.room.updated_at.to_fs(:epoch), member_names: members.map(&:name) }
      },
      placeholders: placeholders.map(&:name),
      shared: other_memberships.map { |m| { room_id: m.room_id, param_key: m.room.model_name.param_key, name: m.room.name, unread: m.unread? } },
      can_create_rooms: user.administrator? || !Account.first&.settings&.restrict_room_creation_to_administrators?
    },
    push_subscriptions: user.push_subscriptions.map { |ps|
      agent = UserAgent.parse(ps.user_agent)
      { id: ps.id, endpoint: ps.endpoint, browser: agent.browser.to_s, version: agent.version.to_s, platform: agent.platform.to_s }
    },
    autocompletable: User.active.with_attached_avatar.ordered.limit(20).map(&:name)
  }
end

def capture(name, session, ext: "html", **meta)
  File.write(File.join(OUT, "#{name}.#{ext}"), session.response.body)
  $cases[name] = meta.merge(status: session.response.status, account: account_facts, data: view_data(meta[:as]),
    users: User.all.to_h { |u| [ u.name, user_facts(u, Rails.application.routes.url_helpers) ] })
end

def new_session(ua: "chrome_mac")
  ActionDispatch::Integration::Session.new(Rails.application).tap do |s|
    s.host! HOST
    s.instance_variable_set(:@ua, USER_AGENTS.fetch(ua))
  end
end

def get(s, path, headers = {})
  s.get path, headers: { "User-Agent" => s.instance_variable_get(:@ua) }.merge(headers)
end

def sign_in(s, email)
  get(s, "/session/new")
  token = s.response.body[/name="authenticity_token" value="([^"]+)"/, 1]
  s.post "/session", params: { email_address: email, password: "secret123456", authenticity_token: token },
    headers: { "User-Agent" => s.instance_variable_get(:@ua) }
  raise "sign in failed for #{email}: #{s.response.status}" unless s.response.status == 302
end

def page(name, as: nil, ua: "chrome_mac", path:, ext: "html", headers: {})
  s = new_session(ua: ua)
  sign_in(s, as) if as
  get(s, path, headers)
  capture(name, s, ext: ext, as: as, ua: ua, path: path, referrer: headers["Referer"])
  s
end

david = "david@37signals.com"
kevin = "kevin@37signals.com"
users = User.all.index_by(&:name)

# Signed out
page "sessions_new", path: "/session/new"
page "sessions_new_email", path: "/session/new?email_address=x%40y.com"
s = new_session
get(s, "/session/new")
token = s.response.body[/name="authenticity_token" value="([^"]+)"/, 1]
s.post "/session", params: { email_address: david, password: "wrong", authenticity_token: token }, headers: { "User-Agent" => CHROME_MAC }
capture "sessions_new_rejected", s, as: nil, ua: "chrome_mac", path: "/session"
page "incompatible_browser", ua: "old_chrome", path: "/session/new"
page "incompatible_browser_apple_messages", ua: "apple_messages", path: "/session/new"
page "sessions_transfer", path: "/session/transfers/#{users["JZ"].transfer_id}"
page "users_new", path: "/join/#{Account.first.join_code}"

# Signed in
page "account_edit_admin", as: david, path: "/account/edit", headers: { "Referer" => "http://#{HOST}/rooms/1" }
page "account_edit_member", as: kevin, path: "/account/edit"
page "bots_index", as: david, path: "/account/bots"
page "bots_new", as: david, path: "/account/bots/new"
page "bots_edit", as: david, path: "/account/bots/#{users["Bender Bot"].id}/edit"
page "custom_styles_edit", as: david, path: "/account/custom_styles/edit"

page "users_show_self", as: david, path: "/users/#{users["David"].id}", headers: { "Referer" => "http://#{HOST}/rooms/#{Room.find_by(name: "HQ").id}" }
page "users_show_member_as_admin", as: david, path: "/users/#{users["JZ"].id}"
page "users_show_member_as_member", as: kevin, path: "/users/#{users["JZ"].id}"
page "users_show_bot", as: david, path: "/users/#{users["Bender Bot"].id}"
page "users_show_deactivated", as: david, path: "/users/#{users["Ex Employee"].id}"
page "users_show_banned", as: david, path: "/users/#{users["Spam Ham"].id}"

%w[ chrome_mac chrome_windows safari_mac safari_ios chrome_android firefox_mac firefox_android edge_windows ].each do |ua|
  page "profile_#{ua}", as: david, ua: ua, path: "/users/me/profile"
end
page "profile_kevin", as: kevin, path: "/users/me/profile"
page "push_subscriptions", as: david, path: "/users/me/push_subscriptions"

page "sidebar_david", as: david, path: "/users/me/sidebar"
page "sidebar_kevin", as: kevin, path: "/users/me/sidebar"
page "sidebar_frame", as: david, path: "/users/me/sidebar", headers: { "Turbo-Frame" => "user_sidebar" }

page "autocompletable_users", as: david, path: "/autocompletable/users"
page "autocompletable_users_json", as: david, path: "/autocompletable/users.json", ext: "json"
page "avatar_david", as: david, path: helpers.user_avatar_path(users["David"].avatar_token), ext: "svg"
page "avatar_three_initials", as: david, path: helpers.user_avatar_path(users["Anna Bea Cole"].avatar_token), ext: "svg"
page "manifest", as: david, path: "/webmanifest.json", ext: "json"
page "service_worker", as: david, path: "/service-worker.js", ext: "js"

# Partials rendered on their own, as broadcasts, rich text and other views do.
def partial(name, ua: "chrome_mac", as: david = "david@37signals.com", ext: "html", **options, &block)
  Rails.application.executor.wrap do
    Current.user = User.find_by(email_address: as)
    renderer = ApplicationController.renderer.new(http_host: HOST, https: false, "HTTP_USER_AGENT" => USER_AGENTS.fetch(ua))
    body = block ? block.call(renderer) : renderer.render(**options)
    File.write(File.join(OUT, "#{name}.#{ext}"), body)
    $cases[name] = { as: as, ua: ua, path: "/", status: 200, account: account_facts, data: view_data(as),
      users: User.all.to_h { |u| [ u.name, user_facts(u, Rails.application.routes.url_helpers) ] } }
  end
end

USER_AGENTS.each_key do |ua|
  partial "browser_settings_#{ua}", ua: ua, partial: "pwa/browser_settings"
  partial "system_settings_#{ua}", ua: ua, partial: "pwa/system_settings"
  partial "install_instructions_#{ua}", ua: ua, partial: "pwa/install_instructions"
end
partial "autocompletables_template", partial: "users/autocompletables/template"
partial "shared_room_unread", partial: "users/sidebars/rooms/shared", locals: { room: Room.find_by(name: "HQ"), unread: true }
partial "shared_room", partial: "users/sidebars/rooms/shared", locals: { room: Room.find_by(name: "All Talk") }
partial "ban_button_banned", partial: "users/ban_button", assigns: { user: users["Spam Ham"] }, locals: { user: users["Spam Ham"] }
partial "mention", partial: "users/mention", locals: { user: users["JZ"] }
OG_EMBEDS = {
  "og_embed" => { href: "https://example.com/a?b=1&c=2", url: "https://example.com/image.png", filename: "Example <Title> & more", description: "A description" },
  "og_embed_twitter" => { href: "https://x.com/dhh", url: "https://pbs.twimg.com/profile_images/1/avatar.jpg", filename: "DHH", description: nil },
  "og_embed_long_no_href" => { href: nil, url: nil, filename: "x" * 300, description: "é" * 600 }
}
OG_EMBEDS.each do |name, attributes|
  partial name, partial: "action_text/attachables/opengraph_embed", locals: { opengraph_embed: ActionText::Attachment::OpengraphEmbed.new(**attributes) }
end
File.write(File.join(OUT, "og_embeds.json"), JSON.pretty_generate(OG_EMBEDS))
partial("action_text_content") { |_| ActionText::RichText.new(body: "<p>Hi <strong>there</strong></p>").body.to_s }
partial("user_json", ext: "json") { |r| r.render(partial: "users/user", formats: [ :json ], locals: { user: users["JZ"] }) }
partial("mailer_layout") { |r| r.render(html: "<p>Mail</p>".html_safe, layout: "mailer") }

# State-changing cases last.
s = new_session
sign_in(s, david)
get(s, "/account/edit")
token = s.response.body[/name="authenticity_token" value="([^"]+)"/, 1]
s.patch "/account.#{Account.first.id}", params: { account: { name: Account.first.name }, authenticity_token: token }, headers: { "User-Agent" => CHROME_MAC }
get(s, "/account/edit")
capture "account_edit_notice", s, as: david, ua: "chrome_mac", path: "/account/edit"

newbie = User.create!(name: "New Bie", email_address: "newbie@example.com", password: "secret123456")
newbie.memberships.delete_all
page "welcome", as: "newbie@example.com", path: "/"

Account.first.update!(custom_styles: "body { --x: 1; } a > b { color: red }")
page "custom_styles_layout", as: david, path: "/account/custom_styles/edit"

# Uploaded avatars and logos.
png = Rails.root.join("app/assets/images/campfire-icon.png")
User.find_by(name: "Kevin").avatar.attach(io: File.open(png), filename: "kevin.png")
User.find_by(name: "Bender Bot").avatar.attach(io: File.open(png), filename: "bender.png")
Account.first.logo.attach(io: File.open(png), filename: "logo.png")
page "profile_with_avatar", as: kevin, path: "/users/me/profile"
page "account_edit_with_logo", as: david, path: "/account/edit"
page "account_edit_with_logo_member", as: kevin, path: "/account/edit"
page "bots_edit_with_avatar", as: david, path: "/account/bots/#{users["Bender Bot"].id}/edit"
$cases["bots_edit_with_avatar"][:avatar_url] = "http://#{HOST}" + Rails.application.routes.url_helpers.polymorphic_url(User.find_by(name: "Bender Bot").avatar, only_path: true)
page "sessions_new_with_logo", path: "/session/new"

# Pagination kicks in past 500 people.
now = Time.current
User.insert_all(505.times.map { |i| { name: "Person #{format("%03d", i)}", email_address: "person#{i}@example.com", role: 0, status: 0, created_at: now, updated_at: now } })
page "account_edit_paginated", as: david, path: "/account/edit"
page "account_users_page_2", as: david, path: "/account/users.turbo_stream?page=2", ext: "turbo_stream.html", headers: { "Accept" => "text/vnd.turbo-stream.html" }
$cases["account_users_page_2"][:page_users] = User.active.ordered.without_bots.offset(500).limit(500).map(&:name)
$cases["account_edit_paginated"][:page_users] = User.where(status: [ :active, :banned ]).ordered.without_bots.limit(500).map(&:name)

Account.delete_all
page "first_run", path: "/first_run"

# Shared, run-wide facts.
facts = {
  base_url: "http://#{HOST}",
  app_version: Rails.application.config.app_version,
  vapid_public_key: Rails.configuration.x.vapid.public_key,
  stylesheet_tags: ApplicationController.renderer.render(inline: %(<%= stylesheet_link_tag :all, "data-turbo-track": "reload" %>)),
  importmap_tags: ApplicationController.renderer.render(inline: "<%= javascript_importmap_tags %>"),
  assets: Rails.application.assets.load_path.assets.to_h { |asset| [ asset.logical_path.to_s, "/assets/#{asset.digested_path}" ] },
  user_agents: USER_AGENTS,
  platforms: USER_AGENTS.transform_values { |ua|
    platform = ApplicationPlatform.new(ua)
    %i[ ios? android? mac? windows? chrome? firefox? safari? edge? mobile? desktop? apple_messages? ].to_h { |fact| [ fact.to_s.delete_suffix("?"), platform.public_send(fact) ] }
      .merge(browser: platform.browser, operating_system: platform.operating_system)
  },
  rooms: Room.all.to_h { |room| [ room.name || "direct-#{room.id}", { id: room.id, type: room.type, param_key: room.model_name.param_key, name: room.name, updated_at_epoch: room.updated_at.to_fs(:epoch) } ] },
  memberships: Membership.all.map { |m| { room_id: m.room_id, user_id: m.user_id, involvement: m.involvement, unread: m.unread? } },
  signed_streams: {
    rooms: Turbo::StreamsChannel.signed_stream_name(:rooms),
    user_rooms: User.all.to_h { |u| [ u.name, Turbo::StreamsChannel.signed_stream_name([ u, :rooms ]) ] }
  },
  cases: $cases
}
File.write(File.join(OUT, "facts.json"), JSON.pretty_generate(facts))
puts "wrote #{$cases.size} cases to #{OUT}"
