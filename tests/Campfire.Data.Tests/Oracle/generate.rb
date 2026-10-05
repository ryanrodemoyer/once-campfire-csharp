# Regenerates the oracle files the Campfire.Data tests compare against, using Active Record at the
# reference's pinned Rails revision:
#
#   rails-schema.sql        sqlite_master (every object, in creation order) of a database prepared
#                           the way `bin/rails db:prepare` prepares an empty one: schema.rb loaded,
#                           then schema_migrations and ar_internal_metadata filled in.
#   rails-fixtures.sqlite3  that database with reference/test/fixtures loaded, as `fixtures :all`
#                           and the parity seed's first step load them, WAL checkpointed.
#
#   cd tests/Campfire.Data.Tests/Oracle && bundle install && bundle exec ruby generate.rb
Encoding.default_external = Encoding::UTF_8
Encoding.default_internal = Encoding::UTF_8

require "fileutils"
require "active_record"
require "active_record/connection_adapters/sqlite3_adapter"
require "active_record/fixtures"
require "active_support/testing/time_helpers"
require "bcrypt"

HERE = __dir__
REFERENCE = File.expand_path("../../../reference", HERE)
DATABASE = File.join(HERE, "rails-fixtures.sqlite3")

# reference/config/database.yml
ActiveRecord::Base.configurations = {
  "production" => {
    "primary" => { "adapter" => "sqlite3", "database" => DATABASE, "timeout" => 5000, "default_transaction_mode" => "immediate" }
  }
}
ActiveRecord::Tasks::DatabaseTasks.root = REFERENCE
ActiveRecord::Tasks::DatabaseTasks.env = "production"
ActiveRecord::Migrator.migrations_paths = ActiveRecord::Tasks::DatabaseTasks.migrations_paths = [ File.join(REFERENCE, "db/migrate") ]
ActiveRecord::Schema.verbose = false
db_config = ActiveRecord::Base.configurations.configs_for(env_name: "production", name: "primary")

FileUtils.rm_f(Dir["#{DATABASE}*"])
include ActiveSupport::Testing::TimeHelpers
travel_to Time.utc(2026, 3, 2, 16, 0, 0)

# The parts of `db:prepare` (DatabaseTasks#initialize_database) an empty database goes through.
ActiveRecord::Tasks::DatabaseTasks.create(db_config)
ActiveRecord::Tasks::DatabaseTasks.load_schema(db_config, :ruby, File.join(REFERENCE, "db/schema.rb"))
ActiveRecord::Base.establish_connection(db_config)
connection = ActiveRecord::Base.lease_connection

File.write File.join(HERE, "rails-schema.sql"),
  connection.select_values("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid").map { |sql| "#{sql};\n" }.join

# Just the associations, enums and STI the fixture files lean on (reference/app/models).
class User < ActiveRecord::Base
  enum :role, %i[ member administrator bot ]
  enum :status, %i[ active deactivated banned ], default: :active

  # reference/app/models/user/bot.rb
  def self.generate_bot_token = SecureRandom.alphanumeric(12)
end
class Account < ActiveRecord::Base; end
class Room < ActiveRecord::Base
  belongs_to :creator, class_name: "User"
end
module Rooms
  class Open < Room; end
  class Closed < Room; end
  class Direct < Room; end
end
class Membership < ActiveRecord::Base
  belongs_to :room
  belongs_to :user
end
class Message < ActiveRecord::Base
  belongs_to :room
  belongs_to :creator, class_name: "User"
end
class Boost < ActiveRecord::Base
  belongs_to :message
  belongs_to :booster, class_name: "User"
end
class Search < ActiveRecord::Base
  belongs_to :user
end
class Session < ActiveRecord::Base
  belongs_to :user
end
class Webhook < ActiveRecord::Base
  belongs_to :user
end
module Push
  def self.table_name_prefix = "push_"
  class Subscription < ActiveRecord::Base
    belongs_to :user
  end
end
module ActionText
  class RichText < ActiveRecord::Base
    self.table_name = "action_text_rich_texts"
    belongs_to :record, polymorphic: true
  end
end

fixtures = File.join(REFERENCE, "test/fixtures")
names = Dir[File.join(fixtures, "**/*.yml")].map { |path| path.delete_prefix("#{fixtures}/").delete_suffix(".yml") }.sort
ActiveRecord::FixtureSet.create_fixtures(fixtures, names, "action_text/rich_texts" => ActionText::RichText, "push/subscriptions" => Push::Subscription)

connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
ActiveRecord::Base.connection_handler.clear_all_connections!
FileUtils.rm_f([ "#{DATABASE}-wal", "#{DATABASE}-shm" ])
puts "wrote rails-schema.sql and rails-fixtures.sqlite3 (#{File.size(DATABASE)} bytes)"
