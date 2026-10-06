# The side effects of the reference's model callbacks, in the order Rails runs them, with their
# transaction boundaries: writes (statement kind, table, columns and rows changed), BEGIN, COMMIT
# and ROLLBACK, enqueued jobs, and remote-connection disconnects. LifecycleOracleTests runs the
# same scenarios through Campfire.Data.Lifecycle and compares.
#
# Runs in the reference app on a copy of the parity seed's database:
#
#   mkdir -p $DIR/db $DIR/storage && cp tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3 $DIR/db/production.sqlite3
#   parity/bin/reference runner --storage $DIR tests/Campfire.Data.Tests/Lifecycle/callbacks.rb \
#     > tests/Campfire.Data.Tests/Lifecycle/callbacks.json
#
# Each scenario's `setup` SQL runs first, unrecorded, so both sides start from the same rows.
require "json"
require "active_support/testing/time_helpers"

include ActiveSupport::Testing::TimeHelpers
NOW = Time.utc(2026, 3, 3, 12, 0, 0)
travel_to NOW

# Run jobs nowhere: only their enqueueing is observed. `enqueue.active_job` fires when the job
# reaches the adapter, which for a job enqueued inside a transaction is after the commit.
ActiveJob::Base.queue_adapter = :test

$events = nil

# `INSERT INTO "t" ("a", "b") ...` => `insert t a,b`, `UPDATE "t" SET "a" = ?, "b" = ? ...` =>
# `update t a,b`, `DELETE FROM "t" ...` => `delete t`, each with the rows it changed. Columns are
# sorted: their order in the statement isn't behavior.
def normalize_sql(sql, rows)
  case sql
  when /\A\s*BEGIN/i then "begin"
  when /\A\s*COMMIT/i then "commit"
  when /\A\s*ROLLBACK/i then "rollback"
  when /\A\s*INSERT INTO\s+"?(\w+)"?\s*\(([^)]*)\)/i
    "insert #{$1} #{$2.split(",").map { |c| c.strip.delete('"') }.sort.join(",")} rows=#{rows}"
  when /\A\s*UPDATE\s+"?(\w+)"?\s+SET\s+(.*?)\s+WHERE\s/im
    table, assignments = $1, $2
    columns = assignments.scan(/(?:\A|,)\s*"?(\w+)"?\s*=/).flatten
    "update #{table} #{columns.sort.join(",")} rows=#{rows}"
  when /\A\s*DELETE FROM\s+"?(\w+)"?/i
    "delete #{$1} rows=#{rows}"
  end
end

ActiveSupport::Notifications.subscribe("sql.active_record") do |*, payload|
  next unless $events
  next if payload[:name] == "SCHEMA" || payload[:cached]
  # `row_count` is the rows a statement returned; SQLite's `changes()` is the rows it wrote.
  raw = payload[:connection].instance_variable_get(:@raw_connection)
  event = normalize_sql(payload[:sql], raw.changes)
  $events << event if event
end

ActiveSupport::Notifications.subscribe("enqueue.active_job") do |*, payload|
  next unless $events
  job = payload[:job]
  arguments = job.arguments.map { |argument| argument.respond_to?(:to_global_id) ? argument.id : argument }
  $events << "enqueue #{job.class.name} #{arguments.join(",")}"
end

ActiveSupport::Notifications.subscribe("broadcast.action_cable") do |*, payload|
  next unless $events
  broadcasting, message = payload[:broadcasting], payload[:message]
  if broadcasting.start_with?("action_cable/") && message[:type] == "disconnect"
    user = GlobalID::Locator.locate(broadcasting.delete_prefix("action_cable/"))
    $events << "disconnect #{user.id} reconnect=#{message[:reconnect]}"
  else
    $events << "broadcast #{broadcasting}"
  end
end

SCENARIOS = []

def scenario(name, input = {}, setup: [])
  connection = ActiveRecord::Base.connection
  setup.each { |sql| connection.execute(sql) }
  Current.reset
  $events = []
  error = nil
  begin
    yield input
  rescue => e
    error = e.class.name
  ensure
    events, $events = $events, nil
  end
  SCENARIOS << { name: name, input: input, setup: setup, events: events, error: error }.compact
end

def user(id) = User.find(id)
def room(id) = Room.find(id)
def mention(id) = ActionText::Attachment.from_attachable(user(id)).to_html

# messages_controller.rb#create: `create_with_attachment!`, then `deliver_webhooks_to_bots`.
# (`broadcast_create` renders the message: the controller's, not a callback.)
def create_message(input)
  Current.user = user(input[:creator_id])
  room = room(input[:room_id])
  attributes = { client_message_id: input[:client_message_id] }
  attributes[:body] = input[:body_html] unless input[:body_html].nil?
  message = room.messages.create_with_attachment!(attributes)
  bots = room.direct? ? room.users.active_bots : message.mentionees.active_bots
  bots.excluding(message.creator).each { |bot| bot.deliver_webhook_later(message) }
end

DAVID, JASON, BENDER, KEVIN, JZ, MALLORY, DEPLOY_BOT = 127326141, 149087659, 394959859, 712064548, 773523953, 773523955, 773523956
HQ, BENDER_KEVIN, ALL_TALK, DESIGNERS, ARCHIVE = 201306877, 340026324, 486777696, 654632876, 699448327

connected = "UPDATE memberships SET connected_at = '2026-03-03 11:59:30', connections = 1 WHERE room_id = #{ARCHIVE} AND user_id = #{KEVIN}"

scenario "message_create_mentioning_bots",
  { room_id: ARCHIVE, creator_id: DAVID, client_message_id: "c1a7e2b0-0000-4000-8000-000000000001",
    body_html: "<div>Hi #{mention(BENDER)} and #{mention(DEPLOY_BOT)}</div>", plain_text_body: "Hi @Bender Bot and @Deploy Bot",
    mentioned_user_ids: [ BENDER, DEPLOY_BOT ] },
  setup: [ connected ] do |input|
  create_message(input)
end

scenario "message_create_in_direct_room_with_bot",
  { room_id: BENDER_KEVIN, creator_id: KEVIN, client_message_id: "c1a7e2b0-0000-4000-8000-000000000002",
    body_html: "<div>Ping</div>", plain_text_body: "Ping", mentioned_user_ids: [] } do |input|
  create_message(input)
end

scenario "message_create_by_bot_mentioning_itself",
  { room_id: ARCHIVE, creator_id: BENDER, client_message_id: nil,
    body_html: "<div>I am #{mention(BENDER)}</div>", plain_text_body: "I am @Bender Bot", mentioned_user_ids: [ BENDER ] } do |input|
  create_message(input)
end

# No body param: no rich text row.
scenario "message_create_without_body",
  { room_id: HQ, creator_id: JZ, client_message_id: "c1a7e2b0-0000-4000-8000-000000000004",
    body_html: nil, plain_text_body: "", mentioned_user_ids: [] } do |input|
  create_message(input)
end

# messages_controller.rb#update
scenario "message_update_body", { message_id: 933434481, body_html: "<div>Edited</div>", plain_text_body: "Edited" } do |input|
  Message.find(input[:message_id]).update!(body: input[:body_html])
end

# messages/boosts_controller.rb#create
scenario "boost_create", { message_id: 933434481, booster_id: DAVID, content: "👍" } do |input|
  Current.user = user(input[:booster_id])
  Message.find(input[:message_id]).boosts.create!(content: input[:content])
end

# messages/boosts_controller.rb#destroy
boost = Boost.find_by!(message_id: 136976342)
scenario "boost_destroy", { boost_id: boost.id } do |input|
  Boost.find(input[:boost_id]).destroy!
end

# messages_controller.rb#destroy, of a message with six boosts and a body.
scenario "message_destroy", { message_id: 933434507 } do |input|
  Message.find(input[:message_id]).destroy
end

# users_controller.rb#create and first_runs_controller.rb
scenario "user_create", { name: "Nina", email_address: "nina@37signals.com", password: "secret123456" } do |input|
  User.create!(name: input[:name], email_address: input[:email_address], password: input[:password])
end

# accounts/bots_controller.rb#create
scenario "bot_create", { name: "Hook Bot", webhook_url: "https://example.com/hook" } do |input|
  User.create_bot!(name: input[:name], webhook_url: input[:webhook_url])
end

scenario "user_deactivate", { user_id: JASON } do |input|
  user(input[:user_id]).deactivate
end

ban_sessions = [ "198.51.100.7", "198.51.100.7", "203.0.113.20", "", nil ].each_with_index.map do |ip, i|
  ip_sql = ip.nil? ? "NULL" : "'#{ip}'"
  "INSERT INTO sessions (user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES (#{KEVIN}, 'lifecycle-ban-#{i}', #{ip_sql}, 'Firefox', '2026-03-03 10:00:00', '2026-03-03 10:00:00', '2026-03-03 10:00:00')"
end

scenario "user_ban", { user_id: KEVIN }, setup: ban_sessions do |input|
  user(input[:user_id]).ban
end

# A session from a private address fails the ban's validation, which rolls the ban back.
private_session = "INSERT INTO sessions (user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES (#{JZ}, 'lifecycle-ban-private', '10.1.2.3', 'Firefox', '2026-03-03 10:00:00', '2026-03-03 10:00:00', '2026-03-03 10:00:00')"
scenario "user_ban_from_private_address", { user_id: JZ }, setup: [ private_session ] do |input|
  user(input[:user_id]).ban
end

scenario "user_unban", { user_id: MALLORY } do |input|
  user(input[:user_id]).unban
end

# rooms/closeds_controller.rb#update: `memberships.revise(granted:, revoked:)`
scenario "memberships_revise", { room_id: ALL_TALK, granted_user_ids: [ KEVIN ], revoked_user_ids: [ BENDER ] } do |input|
  room(input[:room_id]).memberships.revise(granted: User.where(id: input[:granted_user_ids]), revoked: User.where(id: input[:revoked_user_ids]))
end

scenario "memberships_revoke", { room_id: DESIGNERS, user_ids: [ JZ, MALLORY ] } do |input|
  room(input[:room_id]).memberships.revoke_from(User.where(id: input[:user_ids]))
end

# Ban's `ip_address_is_public` on addresses in the forms IPAddr parses.
BAN_ADDRESSES = [
  "203.0.113.9", "8.8.8.8", "127.0.0.1", "127.255.0.1", "10.0.0.1", "172.16.5.4", "172.32.0.1", "192.168.1.1", "169.254.1.1",
  "0.0.0.0", "255.255.255.255", "010.1.1.1", "1.2.3", "1.2.3.4.5", "256.1.1.1", "1.2.3.4 ", " 1.2.3.4", "1.2.3.4\n", "",
  "abc", "::1", "::", "2001:db8::1", "2606:4700::1111", "fc00::1", "fd12:3456::1", "fe80::1", "febf::1", "fec0::1",
  "::ffff:127.0.0.1", "::ffff:10.0.0.1", "::ffff:8.8.8.8", "::ffff:169.254.0.1", "1:2:3:4:5:6:7:8", "1:2:3:4:5:6:1.2.3.4",
  "::ffff:7f00:1", "1::2::3", "1:2:3:4:5:6:7:8:9", "12345::1", "[2001:db8::1]", "[10.0.0.1]", "fe80::1%eth0", "8.8.8.8%eth0",
  "10.0.0.1/8", "8.8.8.8/8", "8.8.8.8/0", "11.0.0.1/7", "8.8.8.8/33", "8.8.8.8/08", "8.8.8.8/255.0.0.0", "10.1.1.1/255.255.0.255",
  "2001:db8::1/64", "fc00::1/6", "FE80::1", "2001:DB8::A", "1.2.3.4/x", "::ffff:1.2.3.4/96", "0::ffff:10.0.0.1"
].map do |ip|
  ban = Ban.new(ip_address: ip, user: User.first)
  ban.validate
  { ip_address: ip, error: ban.errors[:ip_address].first }
end

puts JSON.pretty_generate(now: NOW.utc.iso8601, scenarios: SCENARIOS, ban_addresses: BAN_ADDRESSES)
