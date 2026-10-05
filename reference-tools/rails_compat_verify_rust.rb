# Verifies, with the reference app, the values crates/rails_compat generated
# (target/rails_compat_rust_output.json, written by `cargo test -p rails_compat`).
# A Rust-signed session_token cookie also goes through a full request against the app: it
# authenticates on Rails (switching back to the Rails image keeps people signed in).
#
#   reference-tools/run.sh reference-tools/rails_compat_verify_rust.rb [path/to/rust_output.json]
require_relative "support"

class RailsCompatVerifyRust
  include ReferenceTools

  def initialize(path)
    @output = JSON.parse(File.read(path))
    @failures = []
    @checks = 0
  end

  def run
    reset_database!
    travel_to(Time.iso8601(@output.fetch("now")))

    verify_signed_cookies
    verify_encrypted_cookies
    verify_session_requests
    verify_signed_ids
    verify_sgids
    verify_turbo_stream_names
    verify_passwords
    verify_app_verifiers

    travel_back
    report
  end

  private
    def check(label, actual, expected)
      @checks += 1
      @failures << "#{label}: expected #{expected.inspect}, got #{actual.inspect}" unless actual == expected
    end

    def verify_signed_cookies
      @output["signed_cookies"].each do |cookie|
        check "signed cookie #{cookie["value"].inspect}", read_cookie(:signed, cookie["name"], cookie["raw"]), cookie["value"]
        check "signed cookie #{cookie["value"].inspect} under another name", read_cookie(:signed, "other_name", cookie["raw"]), nil
      end
    end

    def verify_encrypted_cookies
      @output["encrypted_cookies"].each do |cookie|
        check "encrypted cookie #{cookie["value"].inspect}", read_cookie(:encrypted, cookie["name"], cookie["raw"]), cookie["value"]
        check "encrypted cookie #{cookie["value"].inspect} under another name", read_cookie(:encrypted, "other_name", cookie["raw"]), nil
      end
    end

    def verify_session_requests
      # The Rust app checks Sec-Fetch-Site rather than CSRF tokens, so there are no Rust-made tokens
      # to check; its session cookies are covered by verify_encrypted_cookies.
      session = @output["session"]
      Session.create!(user: @david, token: session["session_token"], user_agent: USER_AGENT, ip_address: "127.0.0.1")
      status, headers, _ = perform(:get, "/", cookies: { "session_token" => session["session_token_cookie"] })
      check "Rust-signed session_token authenticates (not a redirect to sign in)", [ status, headers["location"].to_s.include?("/session/new") ], [ status, false ]
      check "Rust-signed session_token authenticates (status)", status < 400, true

      status, headers, _ = perform(:get, "/", cookies: { "session_token" => session["session_token_cookie"].reverse })
      check "tampered session_token redirects to sign in", headers["location"].to_s.include?("/session/new"), true
    end

    def verify_signed_ids
      @output["signed_ids"].each do |signed|
        purpose = User.combine_signed_id_purposes(signed["purpose"])
        check "signed id #{signed["id"]} #{signed["purpose"]}", User.signed_id_verifier.verified(signed["signed_id"], purpose: purpose), signed["id"]
        check "signed id #{signed["id"]} with another purpose", User.signed_id_verifier.verified(signed["signed_id"], purpose: "user/other"), nil
      end
      avatar = @output["signed_ids"].find { |s| s["purpose"] == "avatar" && s["id"] == @david.id }
      check "find_signed! for avatar", User.find_signed!(avatar["signed_id"], purpose: :avatar), @david
    end

    def verify_sgids
      @output["sgids"].each do |sgid|
        check "sgid #{sgid["expected"]}", SignedGlobalID.parse(sgid["sgid"], for: sgid["purpose"])&.uri&.to_s, sgid["expected"]
      end
      attachable = @output["sgids"].first["sgid"]
      check "ActionText::Attachable.from_attachable_sgid", ActionText::Attachable.from_attachable_sgid(attachable), @david
      check "Rust attachable_sgid equals Rails'", attachable, @david.attachable_sgid
    end

    def verify_turbo_stream_names
      @output["turbo_stream_names"].each do |stream|
        check "turbo stream #{stream["expected"]}", Turbo::StreamsChannel.verified_stream_name(stream["signed"]), stream["expected"]
      end
    end

    def verify_passwords
      @output["passwords"].each do |password|
        check "bcrypt #{password["password"][0, 20]}", BCrypt::Password.new(password["digest"]).is_password?(password["password"]), true
        check "bcrypt wrong password", BCrypt::Password.new(password["digest"]).is_password?("wrong"), false
      end
      user = User.new(password_digest: @output["passwords"].first["digest"])
      check "has_secure_password authenticate", user.authenticate(@output["passwords"].first["password"]), user
    end

    def verify_app_verifiers
      @output["app_verifiers"].each do |signed|
        value = app.message_verifier(signed["name"]).verified(signed["message"], purpose: signed["purpose"])
        check "app verifier #{signed["data_json"]}", ActiveSupport::JSON.encode(value), signed["data_json"]
      end
      blob_id = @output["app_verifiers"].find { |s| s["purpose"] == "blob_id" }["message"]
      check "ActiveStorage::Blob signed id", ActiveStorage::Blob.signed_id_verifier.verified(blob_id, purpose: :blob_id), 42
    end

    def report
      puts "#{@checks} checks, #{@failures.size} failures"
      @failures.each { |failure| puts "  FAIL #{failure}" }
      # Exiting inside `rails runner`'s executor trips the error reporter, so exit afterwards.
      status = @failures.empty?
      at_exit { exit(status) }
    end
end

default = File.expand_path("../target/rails_compat_rust_output.json", __dir__)
default = "/work/target/rails_compat_rust_output.json" unless File.exist?(default)
RailsCompatVerifyRust.new(ARGV.first || default).run
