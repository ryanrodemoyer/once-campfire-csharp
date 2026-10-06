# Runs reference/app/models/message/pagination.rb, the reference's own concern, with Active Record
# at the reference's pinned Rails revision, on the parity seed, and writes what each scope returns
# for PaginationOracleTests to compare the C# pages with:
#
#   pagination.json  { "labels": { "busy_001": id, ... }, "cases": [{ "query", "args", "result" }] }
#                    records are ids; times are milliseconds since the epoch, as Rooms::Refreshes
#                    takes `since`
#
#   cd tests/Campfire.Data.Tests/Oracle && bundle install && bundle exec ruby pagination.rb
#
# It only reads parity-seed.sqlite3.
Encoding.default_external = Encoding::UTF_8
Encoding.default_internal = Encoding::UTF_8
ENV["TZ"] = "UTC"

require "json"
require "active_record"
require "active_record/connection_adapters/sqlite3_adapter"

HERE = __dir__
REFERENCE = File.expand_path("../../../reference", HERE)

ActiveRecord.default_timezone = :utc
ActiveRecord::Base.establish_connection(adapter: "sqlite3", database: File.join(HERE, "parity-seed.sqlite3"), readonly: true)

class ApplicationRecord < ActiveRecord::Base
  self.abstract_class = true
end

class Room < ApplicationRecord
  self.inheritance_column = nil
  has_many :messages
end

# reference/app/models/message.rb, as far as pagination needs it.
class Message < ApplicationRecord
  belongs_to :room
  scope :ordered, -> { order(:created_at) }
end
require File.join(REFERENCE, "app/models/message/pagination")
Message.include Message::Pagination

class RichText < ApplicationRecord
  self.table_name = "action_text_rich_texts"
end

cases = []
record = ->(query, args, result) { cases << { query: query, args: args, result: result } }
ids = ->(messages) { messages.map(&:id) }
ms = ->(time) { (time.to_r * 1000).floor }
since = ->(millis) { Time.at(0, millis, :millisecond) }

# The seed's `busy_NNN` labels (reference-rust/parity/seeds/default.rb): the watercooler messages
# whose body starts "<p>NNN. ".
labels = RichText.where(record_type: "Message", name: "body").where("body LIKE '<p>___. %'")
  .filter_map { |text| [ "busy_#{text.body[3, 3]}", text.record_id ] if text.body.match?(/\A<p>\d{3}\. /) }
  .sort.to_h

Room.order(:id).each do |room|
  record["LastPage", [ room.id ], ids[room.messages.last_page]]
  record["FirstPage", [ room.id ], ids[room.messages.first_page]]
  record["IsPaged", [ room.id ], room.messages.paged?]

  messages = room.messages.order(:id).to_a
  messages.each do |message|
    record["PageBefore", [ room.id, message.id ], ids[room.messages.page_before(message)]]
    record["PageAfter", [ room.id, message.id ], ids[room.messages.page_after(message)]]
    record["PageAround", [ room.id, message.id ], ids[room.messages.page_around(message)]]
    record["ExistsBefore", [ room.id, message.id ], room.messages.before(message).exists?]
    record["ExistsAfter", [ room.id, message.id ], room.messages.after(message).exists?]
  end

  # Rooms::RefreshesController#show
  times = messages.flat_map { |message| [ ms[message.created_at], ms[message.updated_at] ] }
  times = (times.flat_map { |time| [ time - 1, time, time + 1 ] } + [ 0 ]).uniq.sort
  times.each do |millis|
    created = room.messages.page_created_since(since[millis])
    updated = room.messages.without(created).page_updated_since(since[millis])
    record["PageCreatedSince", [ room.id, millis ], ids[created]]
    record["PageUpdatedSince", [ room.id, millis, ids[created] ], ids[updated]]
  end
end

# One case per line, so a regenerated file diffs by case.
File.write(File.join(HERE, "pagination.json"), <<~JSON)
  {
  "labels": #{JSON.generate(labels)},
  "cases": [
  #{cases.map { |c| JSON.generate(c) }.join(",\n")}
  ]
  }
JSON
puts "#{labels.size} labels, #{cases.size} cases"
