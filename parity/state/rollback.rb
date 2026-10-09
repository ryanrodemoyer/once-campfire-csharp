# Boots the reference app on a database Campfire C# wrote, and reads, edits,
# searches and deletes through Active Record.
# Verified as part of task Q03 (acceptance criterion).
ActiveJob::Base.queue_adapter = :test
def check(what) = (yield or raise "rollback: #{what}")

message = Message.find_by(client_message_id: "csharp-1") || Message.find_by!(client_message_id: "rust-1")
check("message body") { message.plain_text_body.include?("hovercraft") }
check("message room and creator") { message.room.name == "Designers" && message.creator.name == "David" }
check("boost") { message.boosts.sole.then { _1.content == "🦀" && _1.booster.name == "Jason" } }
check("search finds the C# message") { Message.search("hovercraft").include?(message) }

csharp_user = User.find_by(email_address: "csharp@example.com") || User.find_by!(email_address: "rusty@example.com")
check("password") { csharp_user.authenticate("secret123456") }
check("session") { csharp_user.sessions.sole.ip_address == "8.8.8.8" }
check("search record") { csharp_user.searches.pluck(:query) == [ "hovercraft" ] }
room = Room.find_by(name: "C# Room") || Room.find_by!(name: "Rust Room")
check("closed room") { room.is_a?(Rooms::Closed) && room.users.pluck(:name).sort == [ "David", csharp_user.name ].sort }
check("account settings") { Account.first.settings.restrict_room_creation_to_administrators == true }

Current.user = csharp_user
message.update!(body: "Edited by Rails zeppelin")
check("edit is searchable") { Message.search("zeppelin").include?(message) && Message.search("hovercraft").exclude?(message) }
message.destroy!
check("delete") { Message.search("zeppelin").none? && Boost.where(message_id: message.id).none? }
puts "rollback ok"
