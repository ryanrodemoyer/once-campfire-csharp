# Runs the reference app's scopes and model queries on the parity seed and writes what they return,
# for QueryOracleTests to compare the C# queries with:
#
#   queries.json         [{ "query": name, "args": [...], "result": ... }, ...]; records are ids
#   parity-seed.sqlite3  the seed database they ran on
#
# Build the seed with the reference app, then run this with it (PARITY_RUNTIME=native or docker):
#
#   parity/bin/seed build default
#   tmp=$(mktemp -d) && cp -r parity/.seed/default/. "$tmp"
#   parity/bin/reference runner --storage "$tmp" tests/Campfire.Data.Tests/Oracle/queries.rb \
#     tests/Campfire.Data.Tests/Oracle/queries.json
#   cp "$tmp/db/production.sqlite3" tests/Campfire.Data.Tests/Oracle/parity-seed.sqlite3
#
# Everything here only reads, except the bot authentication cases, which read too.
require "json"

out = ARGV.fetch(0)
cases = []
record = ->(query, args, result) { cases << { query: query, args: args, result: result } }
ids = ->(relation) { relation.map(&:id) }

users = User.order(:id).to_a
rooms = Room.order(:id).to_a
messages = Message.order(:id).to_a

# -- Users ------------------------------------------------------------------------------------------

record["Users.ActiveOrdered", [], ids[User.active.ordered]]
record["Users.ActiveIds", [], User.active.pluck(:id)]
record["Users.ActiveOrderedWithoutBots", [], ids[User.active.ordered.without_bots]]
record["Users.WithStatusesOrderedWithoutBots", [ [ 0, 2 ] ], ids[User.where(status: [ :active, :banned ]).ordered.without_bots]]
record["Users.ActiveBotsOrdered", [], ids[User.active_bots.ordered]]
users.each do |user|
  record["Users.Find", [ user.id ], user.id]
  record["Users.FindActive", [ user.id ], User.active.find_by(id: user.id)&.id]
  record["Users.FindActiveBot", [ user.id ], User.active_bots.find_by(id: user.id)&.id]
  if user.email_address
    record["Users.FindActiveByEmailAddress", [ user.email_address ], User.active.find_by(email_address: user.email_address)&.id]
  end
end
[ "", "a", "Da", "BOT", "%", "_a", "o B", "zzz" ].each do |query|
  record["Users.ActiveFilteredByOrdered", [ query ], ids[User.active.filtered_by(query).ordered]]
end

bot_keys = users.select(&:bot?).flat_map { |bot| [ bot.bot_key, "#{bot.id}-wrong", "#{bot.id}", " #{bot.id}-#{bot.bot_token}", "#{bot.id}abc-#{bot.bot_token}", "#{bot.bot_key}-extra" ] }
bot_keys += [ "", "-", "--", "x-y", "0-0", "#{users.first.id}-" ]
bot_keys.each { |key| record["Users.AuthenticateBot", [ key ], User.authenticate_bot(key)&.id] }

excluded = [ [], users.first(2).map(&:id), users.map(&:id) ]
excluded.each do |exclude|
  [ 0, 3, 20 ].each do |limit|
    record["Users.ActiveExcludingByCreation", [ exclude, limit ], ids[User.active.where.not(id: exclude).order(:created_at).limit(limit)]]
  end
end

# -- Rooms ------------------------------------------------------------------------------------------

record["Rooms.Count", [], Room.count]
record["Rooms.Original", [], Room.original&.id]
{ "Open" => Rooms::Open, "Closed" => Rooms::Closed, "Direct" => Rooms::Direct }.each do |type, klass|
  record["Rooms.OfType", [ type ], ids[Room.where(type: klass.name)]]
  record["Rooms.IdsOfType", [ type ], klass.pluck(:id)]
  record["Rooms.CountOfType", [ type ], klass.count]
end

users.each do |user|
  record["Rooms.ForUser", [ user.id ], ids[user.rooms]]
  record["Rooms.OriginalForUser", [ user.id ], user.rooms.original&.id]
  record["Rooms.ForUserWithoutDirects", [ user.id ], ids[user.rooms.without_directs]]
  record["Rooms.ForUserWithoutDirectsOrdered", [ user.id ], ids[user.rooms.without_directs.ordered]]
  { "Open" => :opens, "Closed" => :closeds, "Direct" => :directs }.each do |type, scope|
    record["Rooms.ForUserOfType", [ user.id, type ], ids[user.rooms.public_send(scope)]]
    record["Rooms.IdsForUserOfType", [ user.id, type ], user.rooms.public_send(scope).pluck(:id)]
  end
  rooms.each do |room|
    record["Rooms.FindForUser", [ user.id, room.id ], user.rooms.find_by(id: room.id)&.id]
    record["Rooms.FindForUserWithoutDirects", [ user.id, room.id ], user.rooms.without_directs.find_by(id: room.id)&.id]
  end
end

direct_user_sets = Rooms::Direct.all.map { |room| room.user_ids.sort }
direct_user_sets += direct_user_sets.map(&:reverse) + direct_user_sets.map { |set| set.first(1) } + [ [], users.map(&:id) ]
direct_user_sets.uniq.each do |user_ids|
  record["Rooms.FindDirectFor", [ user_ids ], Rooms::Direct.send(:find_for, User.where(id: user_ids).to_a)&.id]
end

rooms.each do |room|
  record["Users.InRoom", [ room.id ], ids[room.users]]
  record["Users.IdsInRoom", [ room.id ], room.user_ids]
  record["Users.ActiveBotsInRoom", [ room.id ], ids[room.users.active_bots]]
  record["Users.InRoomWhereIds", [ room.id, users.map(&:id) ], ids[room.users.where(id: users.map(&:id))]]
  record["Memberships.ForRoom", [ room.id ], ids[room.memberships]]
  record["Messages.InRoomOrdered", [ room.id ], ids[room.messages.ordered]]
  record["Messages.CountInRoom", [ room.id ], room.messages.count]
end

# -- Memberships ------------------------------------------------------------------------------------

record["Memberships.Count", [], Membership.count]
record["Memberships.Connected", [], ids[Membership.connected]]
record["Memberships.Disconnected", [], ids[Membership.disconnected]]
users.each do |user|
  record["Memberships.ForUser", [ user.id ], ids[user.memberships]]
  record["Memberships.ForUserWithOrderedRoom", [ user.id, false ], ids[user.memberships.with_ordered_room]]
  record["Memberships.ForUserWithOrderedRoom", [ user.id, true ], ids[user.memberships.visible.with_ordered_room]]
  record["Memberships.CountUnreadForUser", [ user.id ], user.memberships.unread.count]
  record["Memberships.CountForUserWithoutDirectRooms", [ user.id ], user.memberships.without_direct_rooms.count]
  direct_ids = user.rooms.directs.pluck(:id)
  record["Memberships.UserIdsInRooms", [ direct_ids ], Membership.where(room_id: direct_ids).pluck(:user_id).uniq]
  rooms.each do |room|
    record["Memberships.FindFor", [ user.id, room.id ], user.memberships.find_by(room_id: room.id)&.id]
  end
end

# -- Messages, boosts, rich texts and attachments ---------------------------------------------------

users.each { |user| record["Messages.ByCreator", [ user.id ], ids[user.messages]] }
record["Messages.WhereIds", [ messages.first(5).map(&:id) ], ids[Message.where(id: messages.first(5).map(&:id))]]
messages.each do |message|
  record["Boosts.ForMessageOrdered", [ message.id ], ids[message.boosts.ordered]]
  record["RichTexts.For", [ "Message", message.id, "body" ], message.rich_text_body&.id]
  record["Attachments.For", [ "Message", message.id, "attachment" ], message.attachment_attachment&.id]
end
users.each do |user|
  record["Boosts.ByBooster", [ user.id ], ids[user.boosts]]
  record["Attachments.For", [ "User", user.id, "avatar" ], user.avatar_attachment&.id]
end
ActiveStorage::Blob.order(:id).each do |blob|
  record["VariantRecords.ForBlobs", [ [ blob.id ] ], ids[blob.variant_records]]
  record["Attachments.ForBlob", [ blob.id ], ids[blob.attachments]]
end

# -- Push subscriptions (Room::MessagePusher) -------------------------------------------------------

pusher = ->(room, message) { Room::MessagePusher.new(room: room, message: message) }
messages.each do |message|
  room = message.room
  everything = pusher[room, message].send(:push_subscriptions_for_users_involved_in_everything)
  record["PushSubscriptions.ForRoomPush", [ room.id, message.creator_id, "everything", nil ], ids[everything]]
  mentionees = User.where(id: room.user_ids)
  mentions = pusher[room, message].send(:push_subscriptions_for_mentionable_users, mentionees)
  record["PushSubscriptions.ForRoomPush", [ room.id, message.creator_id, "mentions", mentionees.ids ], ids[mentions]]
end
users.each { |user| record["PushSubscriptions.ForUser", [ user.id ], ids[user.push_subscriptions]] }

# -- Sessions, bans, searches, webhooks and the account ---------------------------------------------

Session.order(:id).each do |session|
  record["Sessions.FindByToken", [ session.token ], Session.find_by(token: session.token)&.id]
end
users.each do |user|
  record["Sessions.ForUser", [ user.id ], ids[user.sessions]]
  record["Sessions.IpAddressesForUser", [ user.id ], user.sessions.pluck(:ip_address).compact_blank.uniq]
  record["Bans.ForUser", [ user.id ], ids[user.bans]]
  record["Searches.ForUserOrdered", [ user.id ], ids[user.searches.ordered]]
  record["Webhooks.ForUser", [ user.id ], user.webhook&.id]
end
[ "203.0.113.9", "127.0.0.1", "", nil ].each { |ip| record["Bans.IsBanned", [ ip ], Ban.banned?(ip)] }

account = Account.first
record["Accounts.First", [], account.id]
record["Account.Settings", [ account.id ], { "json" => account.settings.instance_variable_get(:@data).to_json, "restrict" => account.settings.restrict_room_creation_to_administrators? }]

File.write(out, JSON.pretty_generate(cases) + "\n")
puts "#{cases.size} cases -> #{out}"
