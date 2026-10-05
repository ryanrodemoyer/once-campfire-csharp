# Renders every views-B screen in the reference app and writes one golden per case to OUT
# (crates/views/tests/golden/b): the view-model input the Rust templates take, the page
# context, and the reference response as a canonical token stream (see canonical.rb).
#
#   reference-tools/views/b/run.sh   # loads fixtures, runs setup.rb, then this
require_relative "session"
require_relative "canonical"
require_relative "inputs"

out = Pathname(ENV.fetch("OUT")).tap(&:mkpath)
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
TURBO_STREAM = { "Accept" => "text/vnd.turbo-stream.html, text/html, application/xhtml+xml" }

users = User.all.index_by(&:name)
david, kevin, bender = users.values_at("David", "Kevin", "Bender Bot")
rooms = Room.all.index_by { |r| r.name || r.users.order(:name).pluck(:name).join("+") }
designers, pets, hq, watercooler = rooms.values_at("Designers", "All Pets", "HQ", "All Talk")
david_jason = rooms["David+Jason"]
bender_kevin = rooms["Bender Bot+Kevin"]
msg = ->(cid) { Message.find_by!(client_message_id: cid) }

def write_golden(out, name, kind:, template:, user:, session:, input:, headers: {})
  Rails.application.executor.wrap { write_golden!(out, name, kind:, template:, user:, session:, input: input.respond_to?(:call) ? input.call : input) }
end

def write_golden!(out, name, kind:, template:, user:, session:, input:)
  body = session.response.body
  golden = {
    name: name,
    template: template,
    kind: kind,
    status: session.response.status,
    context: ViewsB::Inputs.context(user, session),
    input: input,
    expected: kind == "json" ? JSON.parse(body) : ViewsB::Canonical.tokens(body, document: kind == "page"),
    raw: (body if kind == "json")
  }
  File.write(out.join("#{name}.json"), JSON.pretty_generate(golden))
  puts "#{name}: #{session.response.status}"
end

def fetch(user, verb, path, headers: {}, **options)
  ViewsB.session_for(user).tap do |s|
    s.public_send(verb, path, headers: { "User-Agent" => UA }.merge(headers), **options)
  end
end

I = ViewsB::Inputs

# GET screens, in a stable database state.
[
  [ "rooms_show_closed", "rooms/show", david, "/rooms/#{designers.id}", ->(s) { I.room_show(designers, david, s) } ],
  [ "rooms_show_original", "rooms/show", david, "/rooms/#{pets.id}", ->(s) { I.room_show(pets, david, s) } ],
  [ "rooms_show_direct", "rooms/show", david, "/rooms/#{david_jason.id}", ->(s) { I.room_show(david_jason, david, s) } ],
  [ "rooms_show_member", "rooms/show", kevin, "/rooms/#{hq.id}", ->(s) { I.room_show(hq, kevin, s) } ],
  [ "rooms_show_at_message", "rooms/show", david, "/rooms/#{designers.id}/@#{msg["0002"].id}", ->(s) { I.room_show(designers, david, s, around: msg["0002"]) } ],
  [ "rooms_opens_new", "rooms/opens/new", david, "/rooms/opens/new", ->(s) { I.open_form(Rooms::Open.new(name: "New room"), david) } ],
  [ "rooms_opens_edit", "rooms/opens/edit", david, "/rooms/opens/#{hq.id}/edit", ->(s) { I.open_form(hq, david) } ],
  [ "rooms_opens_edit_member", "rooms/opens/edit", kevin, "/rooms/opens/#{hq.id}/edit", ->(s) { I.open_form(hq, kevin) } ],
  [ "rooms_closeds_new", "rooms/closeds/new", david, "/rooms/closeds/new", ->(s) { I.closed_form(Rooms::Closed.new(name: "New room"), david) } ],
  [ "rooms_closeds_edit", "rooms/closeds/edit", david, "/rooms/closeds/#{designers.id}/edit", ->(s) { I.closed_form(designers, david) } ],
  [ "rooms_closeds_edit_member", "rooms/closeds/edit", kevin, "/rooms/closeds/#{designers.id}/edit", ->(s) { I.closed_form(designers, kevin) } ],
  [ "rooms_directs_new", "rooms/directs/new", david, "/rooms/directs/new", ->(s) { {} } ],
  [ "rooms_directs_edit", "rooms/directs/edit", david, "/rooms/directs/#{david_jason.id}/edit", ->(s) { I.direct_edit(david_jason, david) } ],
  [ "rooms_involvements_show", "rooms/involvements/show", david, "/rooms/#{designers.id}/involvement", ->(s) { I.involvement(designers, david) } ],
  [ "rooms_involvements_show_direct", "rooms/involvements/show", david, "/rooms/#{david_jason.id}/involvement", ->(s) { I.involvement(david_jason, david) } ],
  [ "messages_show_text", "messages/show", david, "/rooms/#{designers.id}/messages/#{msg["rich"].id}", ->(s) { I.message(msg["rich"]) } ],
  [ "messages_show_image", "messages/show", david, "/rooms/#{designers.id}/messages/#{msg["att-black_hole.jpg"].id}", ->(s) { I.message(msg["att-black_hole.jpg"]) } ],
  [ "messages_edit_text", "messages/edit", david, "/rooms/#{designers.id}/messages/#{msg["rich"].id}/edit", ->(s) { I.edit(msg["rich"]) } ],
  [ "messages_edit_attachment", "messages/edit", david, "/rooms/#{designers.id}/messages/#{msg["att-moon.jpg"].id}/edit", ->(s) { I.edit(msg["att-moon.jpg"]) } ],
  [ "messages_boosts_new", "messages/boosts/new", david, "/messages/#{msg["emoji"].id}/boosts/new", ->(s) { { message: I.message(msg["emoji"]), user: I.user(david) } } ],
  [ "searches_index", "searches/index", david, "/searches?q=post", ->(s) { I.search(david, "post", s) } ],
  [ "searches_index_empty", "searches/index", david, "/searches", ->(s) { I.search(david, nil, s) } ]
].each do |name, template, user, path, input|
  s = fetch(user, :get, path)
  write_golden out, name, kind: "page", template: template, user: user, session: s, input: -> { input.(s) }
end

# Fragments rendered without the layout.
since = (msg["att-moon.jpg"].created_at.to_f * 1000).to_i - 1
[
  [ "rooms_refreshes_show", "rooms/refreshes/show", david, "/rooms/#{designers.id}/refresh?since=#{since}", TURBO_STREAM, ->(s) { I.refresh(designers, since) } ],
  [ "messages_index", "messages/index", david, "/rooms/#{designers.id}/messages?before=#{msg["rich"].id}", {}, ->(s) { { messages: designers.messages.with_creator.page_before(msg["rich"]).map { |m| I.message(m) } } } ],
  [ "messages_boosts_index", "messages/boosts/index", david, "/messages/#{msg["emoji"].id}/boosts", { "Turbo-Frame" => "boosting_message_emoji" }, ->(s) { I.message(msg["emoji"]) } ]
].each do |name, template, user, path, headers, input|
  s = fetch(user, :get, path, headers: headers)
  kind = s.response.body.include?("<html") ? "page" : "fragment"
  write_golden out, name, kind: kind, template: template, user: user, session: s, input: -> { input.(s) }
end

s = fetch(bender, :get, "/rooms/#{bender_kevin.id}/#{bender.bot_key}/messages")
write_golden out, "messages_by_bots_index", kind: "json", template: "messages/by_bots/index", user: bender, session: s,
  input: bender_kevin.messages.with_creator.last_page.map { |m| I.message_json(m, s) }

# Mutating requests last.
s = ViewsB.session_for(david)
token = ViewsB.csrf_token(s)
s.post "/rooms/#{designers.id}/messages", params: { authenticity_token: token, message: { body: "<div>Fresh <em>message</em></div>", client_message_id: "fresh-1" } }, headers: TURBO_STREAM.merge("User-Agent" => UA)
write_golden out, "messages_create", kind: "fragment", template: "messages/create", user: david, session: s,
  input: { message: I.message(msg["fresh-1"]), room_kind: I.room_kind(designers) }

s.post "/rooms/999999/messages", params: { authenticity_token: token, message: { body: "Lost", client_message_id: "lost-1" } }, headers: TURBO_STREAM.merge("User-Agent" => UA)
write_golden out, "messages_room_not_found", kind: "page", template: "messages/room_not_found", user: david, session: s, input: {}

destroyed = I.message(msg["fresh-1"])
s.delete "/rooms/#{designers.id}/messages/#{msg["fresh-1"].id}", params: { authenticity_token: token }, headers: TURBO_STREAM.merge("User-Agent" => UA)
write_golden out, "messages_destroy", kind: "fragment", template: "messages/destroy", user: david, session: s, input: destroyed

b = fetch(bender, :post, "/rooms/#{watercooler.id}/#{bender.bot_key}/messages/#{msg["0004"].id}/boosts", env: { "RAW_POST_DATA" => "🤖 <&>" }, headers: { "Content-Type" => "text/plain" })
write_golden out, "messages_boosts_by_bots_show", kind: "json", template: "messages/boosts/by_bots/show", user: bender, session: b,
  input: I.boost_json(Boost.order(:id).last, b)

b = fetch(bender, :put, "/rooms/#{bender_kevin.id}/#{bender.bot_key}/messages/#{msg["bot-2"].id}", env: { "RAW_POST_DATA" => "Edited <b>&</b>" }, headers: { "Content-Type" => "text/plain" })
write_golden out, "messages_by_bots_show", kind: "json", template: "messages/by_bots/show", user: bender, session: b,
  input: I.message_json(msg["bot-2"], b)
