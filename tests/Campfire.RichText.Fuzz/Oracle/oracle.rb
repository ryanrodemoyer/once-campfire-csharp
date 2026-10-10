# The reference side of the differential fuzzer: Campfire's real rich text pipeline, as a service.
# Run inside the campfire-reference image under `bin/rails runner` on a fresh schema (the C# driver,
# ReferenceOracle.cs, starts it through parity/bin/reference).
#
# It creates the records reference-tools/richtext/generate.rb creates, the same way, and prints one
# JSON line describing them: their raw rows (so the port can build the same database) and SGIDs
# minted for every variant the fuzzer needs. Then, for every JSON line {"id", "host", "body"} on
# stdin, it stores the body as the raw action_text_rich_texts.body column and prints one JSON line
# with generate.rb's six outputs for it.

require "json"

OUT = $stdout.dup
$stdout.reopen($stderr) # Nothing but answers on the answer stream
OUT.sync = true

Rails.logger.level = :error
ActiveRecord::Base.logger = nil

# generate.rb's outcome
def outcome
  { "ok" => yield }
rescue Exception => e
  cause = e.cause ? " (cause: #{e.cause.class.name})" : ""
  { "error" => e.class.name, "message" => (e.message.to_s.scrub[0, 300] + cause) }
end

# --- Records, as generate.rb creates them --------------------------------------------------------

TIME = Time.utc(2024, 1, 2, 3, 4, 5)

def create_user(key, name, bio: nil)
  User.insert!({ name: name, email_address: "#{key}@example.com", password_digest: BCrypt::Password.create("secret123456", cost: 4),
    bio: bio, role: 0, status: 0, created_at: TIME, updated_at: TIME })
  User.find_by!(email_address: "#{key}@example.com")
end

users = {
  "david" => create_user("david", "David", bio: "Founder"),
  "jason" => create_user("jason", "Jason"),
  "kevin" => create_user("kevin", "Kevin", bio: "Ops"),
  "mallory" => create_user("mallory", "Mallory <b>&amp;</b> \"Evil\"", bio: "<script>alert(1)</script>"),
  "obrien" => create_user("obrien", "Seán O'Brien 🎉")
}
deleted = create_user("deleted", "Deleted")
deleted_sgid = deleted.attachable_sgid
deleted_gid = deleted.to_gid.to_s
deleted.delete

Room.insert!({ name: "Pets", type: "Rooms::Open", creator_id: users["david"].id, created_at: TIME, updated_at: TIME })
room = Room.first
creator = users["jason"]

Message.insert!({ room_id: room.id, creator_id: creator.id, client_message_id: "fuzz", created_at: TIME, updated_at: TIME })
message = Message.find_by!(client_message_id: "fuzz")
connection = ActiveRecord::Base.connection.raw_connection
connection.execute(
  "INSERT INTO action_text_rich_texts (record_type, record_id, name, body, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
  [ "Message", message.id, "body", "", TIME.iso8601, TIME.iso8601 ])
rich_text_id = ActiveRecord::Base.connection.select_value("SELECT id FROM action_text_rich_texts WHERE record_id = #{message.id}")

def tampered(sgid) = "#{sgid.split("--").first}--invalid"

def rails7_sgid(record)
  marshaled = Base64.urlsafe_encode64(Marshal.dump(record.to_gid.to_s))
  payload = { "_rails" => { "message" => marshaled, "exp" => nil, "pur" => "attachable" } }
  "#{Base64.strict_encode64(JSON.generate(payload))}--invalidsignature"
end

def mention_content(user)
  ApplicationController.render partial: "users/mention", locals: { user: user }
end

attachable = ActionText::Attachable::LOCATOR_NAME
sgids = users.flat_map do |key, user|
  [
    [ "#{key} attachable", user.attachable_sgid ],
    [ "#{key} default expiry", user.to_sgid(for: attachable).to_s ],
    [ "#{key} expired", user.to_sgid(expires_at: Time.utc(2020, 1, 1), for: attachable).to_s ],
    [ "#{key} expires 2099", user.to_sgid(expires_at: Time.utc(2099, 1, 1), for: attachable).to_s ],
    [ "#{key} cross purpose", user.to_sgid(expires_in: nil, for: "transfer").to_s ],
    [ "#{key} default purpose", user.to_sgid(expires_in: nil).to_s ],
    [ "#{key} tampered", tampered(user.attachable_sgid) ],
    [ "#{key} rails7", rails7_sgid(user) ],
    [ "#{key} gid", user.to_gid.to_s ]
  ]
end + [
  [ "deleted attachable", deleted_sgid ],
  [ "deleted tampered", tampered(deleted_sgid) ],
  [ "deleted gid", deleted_gid ],
  [ "room attachable", room.to_sgid(expires_in: nil, for: attachable).to_s ],
  [ "room tampered", tampered(room.to_sgid(expires_in: nil, for: attachable).to_s) ],
  [ "room rails7", rails7_sgid(room) ],
  [ "room gid", room.to_gid.to_s ],
  [ "message attachable", message.to_sgid(expires_in: nil, for: attachable).to_s ],
  [ "message tampered", tampered(message.to_sgid(expires_in: nil, for: attachable).to_s) ],
  [ "message gid", message.to_gid.to_s ]
]

rows = %w[ users rooms messages ].to_h { |table| [ table, ActiveRecord::Base.connection.select_all("SELECT * FROM #{table}").to_a ] }

OUT.puts JSON.generate({
  "tables" => rows,
  "users" => users.map { |key, user| { "key" => key, "id" => user.id, "mention_content" => mention_content(user) } },
  "sgids" => sgids.map { |label, sgid| { "label" => label, "sgid" => sgid } }
})

# --- The pipeline, as generate.rb runs it --------------------------------------------------------

def with_request(host)
  request = ActionDispatch::Request.new(Rack::MockRequest.env_for("http://#{host}/"))
  controller = MessagesController.new
  controller.set_request!(request)
  controller.set_response!(ActionDispatch::Response.new)
  Current.set(request: request) do
    ActionText::Content.with_renderer(controller) { yield controller.view_context }
  end
end

def outputs(message, host)
  with_request(host) do |v|
    raw = outcome do
      v.auto_link ERB::Util.html_escape(ContentFilters::TextMessagePresentationFilters.apply(message.body.body)),
        html: { target: "_blank" }, sanitize_options: { tags: MessagesHelper::AUTO_LINK_ALLOWED_TAGS, attributes: MessagesHelper::AUTO_LINK_ALLOWED_ATTRIBUTES }
    end
    {
      "presentation" => outcome { v.message_presentation(message).to_s },
      "presentation_raised" => raw["error"],
      "presentation_raised_message" => raw["message"],
      "plain_text" => outcome { message.body.to_plain_text },
      "editable" => outcome { value = v.send(:render_custom_attachments_in, v.editable_body(message)); value&.to_s },
      "mentioned" => outcome { message.send(:mentioned_users).map(&:id) },
      "filtered" => outcome { ContentFilters::TextMessagePresentationFilters.apply(message.body.body).to_html }
    }
  end
end

# A string Rails produced that isn't valid UTF-8 can't be compared with the port's (a .NET string
# can't hold it), so it's reported as such rather than failing the whole answer
def encodable(value)
  case value
  when Hash then value.transform_values { |v| encodable(v) }
  when Array then value.map { |v| encodable(v) }
  when String then value.valid_encoding? ? value : { "invalid_utf8" => Base64.strict_encode64(value) }
  else value
  end
end

$stdin.each_line do |line|
  request = JSON.parse(line)
  connection.execute("UPDATE action_text_rich_texts SET body = ? WHERE id = ?", [ request.fetch("body"), rich_text_id ])
  ActiveRecord::Base.connection.clear_query_cache # The runner's executor caches queries, and the update bypassed it
  answer = outputs(Message.find(message.id), request.fetch("host"))
  OUT.puts JSON.generate(encodable(answer.merge("id" => request.fetch("id"))))
end
