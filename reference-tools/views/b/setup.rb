# Adds the records the views-B goldens need on top of the test fixtures: attachments of every
# presentation kind, sounds, emoji, rich HTML bodies, awkward names and a search index.
#
#   RAILS_ENV=production bin/rails db:fixtures:load
#   RAILS_ENV=production bin/rails runner reference-tools/views/b/setup.rb
require_relative "prelude"

fixtures = Rails.root.join("test/fixtures/files")
david, jason, jz, kevin = %w[ david jason jz kevin ].map { |n| User.find_by!(name: n == "jz" ? "JZ" : n.capitalize) }
designers = Room.find_by!(name: "Designers")
hq = Room.find_by!(name: "HQ")

kevin.update!(bio: %(Programmer & "tester" <3))
jz.update!(name: "JZ")

base = 3.minutes.ago
at = ->(i) { base + i.seconds }

def attach(room, creator, created_at, io:, filename:, content_type:)
  room.messages.create_with_attachment!(creator: creator, created_at: created_at, client_message_id: "att-#{filename}",
    attachment: { io: io, filename: filename, content_type: content_type })
end

attach designers, david, at[1], io: File.open(fixtures.join("moon.jpg")), filename: "moon.jpg", content_type: "image/jpeg"
attach designers, jason, at[2], io: File.open(fixtures.join("black_hole.jpg")), filename: "black_hole.jpg", content_type: "image/jpeg"
attach designers, david, at[3], io: File.open(fixtures.join("alpha-centuri.mov")), filename: "alpha-centuri.mov", content_type: "video/quicktime"
# Blobs whose analysis found no dimensions take the unconstrained presentation branch.
[ [ "moon.jpg", "image/jpeg", "nodims.jpg" ], [ "alpha-centuri.mov", "video/quicktime", "nodims.mov" ] ].each_with_index do |(file, type, name), i|
  attach(designers, jason, at[4] - (i + 1).seconds, io: File.open(fixtures.join(file)), filename: name, content_type: type)
    .attachment.blob.update!(metadata: { "identified" => true, "analyzed" => true })
end
attach designers, kevin, at[4], io: StringIO.new("hello"), filename: %(notes & "stuff" <1>.txt), content_type: "text/plain"

designers.messages.create!(creator: david, created_at: at[5], client_message_id: "sound-text", body: "/play bell")
designers.messages.create!(creator: jason, created_at: at[6], client_message_id: "sound-image", body: "/play 56k")
designers.messages.create!(creator: jz, created_at: at[7], client_message_id: "emoji", body: "🎉🔥")
designers.messages.create!(creator: kevin, created_at: at[8], client_message_id: "rich",
  body: %(<div>Visit https://example.com/?a=1&amp;b=2 &amp; <strong>bold</strong> "quoted" it's &lt;script&gt;</div><ul><li>one</li></ul>))

emoji_message = designers.messages.find_by!(client_message_id: "emoji")
emoji_message.boosts.create!(booster: kevin, content: "🚀")
emoji_message.boosts.create!(booster: jz, content: %(<b>"hi"</b>))

bender = User.find_by!(name: "Bender Bot")
bender_kevin = Room.directs.detect { |r| r.users.pluck(:name).sort == [ "Bender Bot", "Kevin" ] }
bender_kevin.messages.create!(creator: kevin, created_at: at[10], client_message_id: "bot-1", body: "Hi <b>bot</b> & friends")
bender_kevin.messages.create!(creator: bender, created_at: at[11], client_message_id: "bot-2", body: "Beep boop")

hq.messages.create!(creator: kevin, created_at: at[9], client_message_id: "hq-1", body: "Pizza party in HQ")

Message.find_each { |m| m.send(:update_in_index) rescue nil }
Message.connection.execute "delete from message_search_index"
Message.find_each { |m| m.send(:create_in_index) }

Search.where(user: david).delete_all
Search.create!(user: david, query: "post", updated_at: 2.minutes.ago)
Search.create!(user: david, query: %(pizza & "pie"), updated_at: 1.minute.ago)

puts "ok"
