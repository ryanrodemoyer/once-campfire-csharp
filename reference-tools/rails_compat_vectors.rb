# Generates vectors/rails_compat.json: golden vectors for the signing, encryption and CSRF
# contracts implemented by crates/rails_compat. Every expected value comes from the reference app
# itself (cookie jar, session store, controllers, verifiers), never from a reimplementation.
#
#   reference-tools/run.sh reference-tools/rails_compat_vectors.rb
require_relative "support"
require "action_controller/test_case"

class RailsCompatVectors
  include ReferenceTools

  def generate
    reset_database!

    travel_to(NOW)
    begin
      {
        "secret_key_base" => app.secret_key_base,
        "rotated_secret_key_base" => ROTATED_SECRET_KEY_BASE,
        "now" => iso(NOW),
        "key_generator" => key_generator_vectors,
        "cookie_escaping" => cookie_escaping_vectors,
        "signed_cookies" => signed_cookie_vectors,
        "encrypted_cookies" => encrypted_cookie_vectors,
        "session" => session_vectors,
        "csrf" => csrf_vectors,
        "signed_ids" => signed_id_vectors,
        "global_ids" => global_id_vectors,
        "sgids" => sgid_vectors,
        "unverified_sgids" => unverified_sgid_vectors,
        "turbo_stream_names" => turbo_vectors,
        "app_verifiers" => app_verifier_vectors,
        "passwords" => password_vectors
      }
    ensure
      travel_back
    end
  end

  private
    # --- Key generator ---------------------------------------------------------------------------

    SALTS = [
      [ "signed cookie", 64 ],
      [ "authenticated encrypted cookie", 32 ],
      [ "active_record/signed_id", 64 ],
      [ "signed_global_ids", 64 ],
      [ "turbo/signed_stream_verifier_key", 64 ],
      [ "anything", 16 ]
    ]

    def key_generator_vectors
      SALTS.map do |salt, length|
        { "salt" => salt, "length" => length, "key_hex" => app.key_generator.generate_key(salt, length).unpack1("H*") }
      end
    end

    # --- Cookie values on the wire -------------------------------------------------------------

    def cookie_escaping_vectors
      [ "abc", "a+b/c=d--0f", "with space", "é~*'()!", "a%20b" ].map do |raw|
        _, header = write_cookie { |jar| jar[:plain] = raw }
        { "raw" => raw, "wire" => header[/\Aplain=([^;]*)/, 1], "parsed" => Rack::Utils.parse_cookies_header("plain=#{header[/\Aplain=([^;]*)/, 1]}")["plain"] }
      end + [
        # Rack unescapes "+" to a space when reading, so a raw "+" never survives a round trip.
        { "raw" => nil, "wire" => "a+b", "parsed" => Rack::Utils.parse_cookies_header("plain=a+b")["plain"] },
        { "raw" => nil, "wire" => "bad%zzescape", "parsed" => Rack::Utils.parse_cookies_header("plain=bad%zzescape")["plain"] }
      ]
    end

    # --- Signed cookies --------------------------------------------------------------------------

    SIGNED_VALUES = [
      "e5XWBGGnEJqiHpVCZzbj6bra", "hello world", "a+b/c=d", %(<script>&'"</script>), "é ünïcødé ☃",
      "line sep ", "tab\tnew\nline\u0001", ""
    ]

    def legacy_cookie_verifier(secret = app.key_generator.generate_key("signed cookie"))
      ActiveSupport::MessageVerifier.new(secret, digest: "SHA1", serializer: ActiveSupport::MessageEncryptor::NullSerializer)
    end

    def signed_cookie_vectors
      generate = []

      SIGNED_VALUES.each do |value|
        raw, header = write_cookie { |jar| jar.signed.permanent[:session_token] = { value: value, httponly: true, same_site: :lax } }
        generate << { "name" => "session_token", "value" => value, "expires_at" => iso(20.years.from_now), "raw" => raw, "set_cookie" => header }
      end

      raw, header = write_cookie { |jar| jar.signed[:no_expiry] = "value" }
      generate << { "name" => "no_expiry", "value" => "value", "expires_at" => nil, "raw" => raw, "set_cookie" => header }

      raw, header = write_cookie { |jar| jar.signed[:short] = { value: "short-lived", expires: 1.hour } }
      generate << { "name" => "short", "value" => "short-lived", "expires_at" => iso(1.hour.from_now), "raw" => raw, "set_cookie" => header }

      token = generate.first
      short = generate.last
      rotated_raw, _ = write_cookie(env: rotated_env) { |jar| jar.signed.permanent[:session_token] = "e5XWBGGnEJqiHpVCZzbj6bra" }
      verifier = legacy_cookie_verifier

      cases = generate.map { |g| [ "valid #{g["name"]} #{g["value"].inspect}", g["name"], g["raw"], NOW ] }
      cases += [
        [ "short-lived before expiry", "short", short["raw"], NOW + 59.minutes ],
        [ "short-lived at expiry", "short", short["raw"], NOW + 1.hour ],
        [ "short-lived after expiry", "short", short["raw"], NOW + 2.hours ],
        [ "permanent just before 20 years", "session_token", token["raw"], NOW + 20.years - 1.second ],
        [ "permanent after 20 years", "session_token", token["raw"], NOW + 20.years + 1.second ],
        [ "wrong cookie name", "other_name", token["raw"], NOW ],
        [ "tampered digest", "session_token", tamper_last(token["raw"]), NOW ],
        [ "tampered payload", "session_token", tamper_first(token["raw"]), NOW ],
        [ "digest removed", "session_token", token["raw"].split("--").first, NOW ],
        [ "truncated digest", "session_token", token["raw"][0..-3], NOW ],
        [ "garbage", "session_token", "not a cookie", NOW ],
        [ "empty", "session_token", "", NOW ],
        [ "signed with rotated secret", "session_token", rotated_raw, NOW ],
        [ "sha256 digest", "session_token", sha256_resigned(token["raw"]), NOW ],
        [ "no metadata (pre-5.2), any name", "session_token", verifier.generate(%("legacy")), NOW ],
        [ "no metadata under another name", "whatever", verifier.generate(%("legacy")), NOW ],
        [ "metadata without purpose", "session_token", verifier.generate(%("nopurpose"), expires_at: NOW + 1.day), NOW ],
        [ "metadata without purpose, expired", "session_token", verifier.generate(%("nopurpose"), expires_at: NOW - 1.day), NOW ],
        [ "purpose of another cookie", "session_token", verifier.generate(%("x"), purpose: "cookie.other"), NOW ],
        [ "marshal serialized value", "session_token", verifier.generate(Marshal.dump("marshaled"), purpose: "cookie.session_token"), NOW ],
        [ "marshal serialized value, no metadata", "session_token", verifier.generate(Marshal.dump("marshaled")), NOW ],
        [ "json integer value", "session_token", verifier.generate("123", purpose: "cookie.session_token"), NOW ],
        [ "json object value", "session_token", verifier.generate(%({"a":1}), purpose: "cookie.session_token"), NOW ],
        [ "unparseable value", "session_token", verifier.generate("not json", purpose: "cookie.session_token"), NOW ],
        [ "new-style envelope", "session_token", verifier.generate(ActiveSupport::JSON.encode({ "_rails" => { "data" => "x", "pur" => "cookie.session_token" } })), NOW ]
      ]

      verify = cases.map do |label, name, raw, now|
        value = at(now) { read_cookie(:signed, name, raw) }
        { "case" => label, "name" => name, "raw" => raw, "now" => iso(now), "expected" => value }
      end

      { "generate" => generate, "verify" => verify }
    end

    def tamper_last(string)
      string[0..-2] + (string[-1] == "0" ? "1" : "0")
    end

    def tamper_first(string)
      (string[0] == "A" ? "B" : "A") + string[1..]
    end

    def sha256_resigned(raw)
      data = raw.split("--").first
      "#{data}--#{OpenSSL::HMAC.hexdigest("SHA256", app.key_generator.generate_key("signed cookie"), data)}"
    end

    # --- Encrypted cookies -----------------------------------------------------------------------

    def encrypted_cookie_vectors
      values = [
        { "session_id" => "6d3a2b1c0f9e8d7c6b5a493827161504", "_csrf_token" => "qK3zv7cQ2oYlP4-sX6JtW0nBf9eR1uHaMdLgE5iVy8k" },
        { "session_id" => "abc", "return_to_after_authenticating" => "http://campfire.test/rooms/1?a=1&b=<2>" },
        "plain string", 42, [ 1, "two", nil, true ], { "nested" => { "é" => "☃ " } }
      ]

      generate = values.map do |value|
        raw, header = write_cookie { |jar| jar.encrypted[:_campfire_session] = { value: value, expires: 20.years.from_now } }
        { "name" => "_campfire_session", "value" => value, "expires_at" => iso(20.years.from_now), "raw" => raw, "set_cookie" => header,
          "plaintext" => decrypt_raw(raw) }
      end

      raw, _ = write_cookie { |jar| jar.encrypted[:short] = { value: "short-lived", expires: 1.hour } }
      short = { "name" => "short", "value" => "short-lived", "expires_at" => iso(1.hour.from_now), "raw" => raw, "plaintext" => decrypt_raw(raw) }
      generate << short
      raw, _ = write_cookie { |jar| jar.encrypted[:no_expiry] = "forever" }
      generate << { "name" => "no_expiry", "value" => "forever", "expires_at" => nil, "raw" => raw, "plaintext" => decrypt_raw(raw) }

      session = generate.first
      rotated_raw, _ = write_cookie(env: rotated_env) { |jar| jar.encrypted[:_campfire_session] = { value: values.first, expires: 20.years.from_now } }
      encryptor = ActiveSupport::MessageEncryptor.new(app.key_generator.generate_key("authenticated encrypted cookie", 32), cipher: "aes-256-gcm", serializer: ActiveSupport::MessageEncryptor::NullSerializer)
      encrypted, iv, tag = session["raw"].split("--")

      cases = generate.map { |g| [ "valid #{g["name"]} #{g["value"].inspect[0, 40]}", g["name"], g["raw"], NOW ] }
      cases += [
        [ "short-lived after expiry", "short", short["raw"], NOW + 2.hours ],
        [ "short-lived before expiry", "short", short["raw"], NOW + 30.minutes ],
        [ "wrong cookie name", "other", session["raw"], NOW ],
        [ "tampered ciphertext", session["name"], "#{tamper_first(encrypted)}--#{iv}--#{tag}", NOW ],
        [ "tampered iv", session["name"], "#{encrypted}--#{tamper_first(iv)}--#{tag}", NOW ],
        [ "tampered auth tag", session["name"], "#{encrypted}--#{iv}--#{tamper_first(tag)}", NOW ],
        [ "missing auth tag", session["name"], "#{encrypted}--#{iv}", NOW ],
        [ "truncated auth tag", session["name"], "#{encrypted}--#{iv}--#{tag[0, 12]}", NOW ],
        [ "garbage", session["name"], "garbage", NOW ],
        [ "encrypted with rotated secret", session["name"], rotated_raw, NOW ],
        [ "no metadata (any name)", "whatever", encryptor.encrypt_and_sign(%({"legacy":true})), NOW ],
        [ "marshal value", session["name"], encryptor.encrypt_and_sign(Marshal.dump("marshaled"), purpose: "cookie._campfire_session"), NOW ]
      ]

      verify = cases.map do |label, name, raw, now|
        value = at(now) { read_cookie(:encrypted, name, raw) }
        { "case" => label, "name" => name, "raw" => raw, "now" => iso(now), "expected" => value }
      end

      { "generate" => generate, "verify" => verify }
    end

    def decrypt_raw(raw)
      ActiveSupport::MessageEncryptor.new(app.key_generator.generate_key("authenticated encrypted cookie", 32), cipher: "aes-256-gcm", serializer: ActiveSupport::MessageEncryptor::NullSerializer)
        .send(:decrypt, raw)
    end

    # --- The session cookie and CSRF, end to end through the app -------------------------------

    def session_vectors
      status, headers, body = perform(:get, "/session/new")
      cookies = set_cookies(headers)
      session_raw = cookies.fetch("_campfire_session").fetch("raw")
      meta_token = body[/<meta name="csrf-token" content="([^"]+)"/, 1]
      form_token = body[%r{<form[^>]*action="http://#{HOST}/session"[^>]*>.*?name="authenticity_token" value="([^"]+)"}m, 1]
      session = read_cookie(:encrypted, "_campfire_session", session_raw)

      bad_status, _, _ = perform(:post, "/session", cookies: { "_campfire_session" => session_raw },
        params: { email_address: "david@example.com", password: "secret123456", authenticity_token: "bad" })
      login_status, login_headers, _ = perform(:post, "/session", cookies: { "_campfire_session" => session_raw },
        params: { email_address: "david@example.com", password: "secret123456", authenticity_token: form_token })
      login_cookies = set_cookies(login_headers)
      login_session_token = Session.last.token
      header_status, _, _ = perform(:post, "/session", cookies: { "_campfire_session" => session_raw },
        params: { email_address: "david@example.com", password: "secret123456" }, headers: { "HTTP_X_CSRF_TOKEN" => meta_token })
      cross_origin_status, _, _ = perform(:post, "/session", cookies: { "_campfire_session" => session_raw },
        params: { email_address: "david@example.com", password: "secret123456", authenticity_token: form_token },
        headers: { "HTTP_ORIGIN" => "http://evil.test" })

      {
        "new_status" => status,
        "set_cookie" => cookies.fetch("_campfire_session").fetch("header"),
        "session_cookie_raw" => session_raw,
        "session" => session,
        "csrf_meta_token" => meta_token,
        "session_form_token" => form_token,
        "post_with_bad_token_status" => bad_status,
        "post_with_form_token_status" => login_status,
        "post_with_meta_token_header_status" => header_status,
        "post_with_cross_origin_status" => cross_origin_status,
        "session_token_set_cookie" => login_cookies.dig("session_token", "header"),
        "session_token_raw" => login_cookies.dig("session_token", "raw"),
        "session_token_value" => login_session_token,
        "session_after_login_raw" => login_cookies.dig("_campfire_session", "raw"),
        "session_after_login" => read_cookie(:encrypted, "_campfire_session", login_cookies.dig("_campfire_session", "raw"))
      }
    end

    CSRF_SESSION_TOKEN = "qK3zv7cQ2oYlP4-sX6JtW0nBf9eR1uHaMdLgE5iVy8k"
    OTHER_SESSION_TOKEN = "Zm9vYmFyYmF6cXV4Zm9vYmFyYmF6cXV4Zm9vYmFyYmE"

    def csrf_vectors
      controller = csrf_controller(CSRF_SESSION_TOKEN)
      global_tokens = 3.times.map { controller.send(:form_authenticity_token) }
      form_targets = [
        [ "/session", "post", "/session/new" ],
        [ "http://#{HOST}/session", "post", "/session/new" ],
        [ "/rooms/1/messages", "post", "/rooms/1" ],
        [ "/rooms/1/", "patch", "/rooms/1/edit" ],
        [ "/rooms/1?x=1", "delete", "/rooms/1" ],
        [ "messages", "post", "/rooms/1" ],
        [ "./involvement", "put", "/rooms/1/" ],
        [ "", "post", "/account/edit" ]
      ]
      form_tokens = form_targets.map do |action, method, page_path|
        page = csrf_controller(CSRF_SESSION_TOKEN, path: page_path, method: "GET")
        { "action" => action, "method" => method, "page_path" => page_path,
          "normalized_action_path" => page.send(:normalize_action_path, action),
          "unmasked_hex" => page.send(:per_form_csrf_token, nil, page.send(:normalize_action_path, action), method).unpack1("H*"),
          "token" => page.send(:form_authenticity_token, form_options: { action: action, method: method }) }
      end

      other_global = csrf_controller(OTHER_SESSION_TOKEN).send(:form_authenticity_token)
      raw_real = Base64.urlsafe_decode64(CSRF_SESSION_TOKEN)
      global_hmac = OpenSSL::HMAC.digest("SHA256", raw_real, "!real_csrf_token")
      pad = "\x01".b * 32

      submitted = global_tokens.map { |t| [ "masked global token", t ] } + [
        [ "unmasked session token", CSRF_SESSION_TOKEN ],
        [ "session token masked with a pad", Base64.urlsafe_encode64(pad + xor(pad, raw_real), padding: false) ],
        [ "unmasked global token", Base64.urlsafe_encode64(global_hmac, padding: false) ],
        [ "masked global token, padded", Base64.urlsafe_encode64(Base64.urlsafe_decode64(global_tokens.first)) ],
        [ "masked global token, standard alphabet", global_tokens.first.tr("-_", "+/") ],
        [ "token for another session", other_global ],
        [ "tampered masked token", tamper_first(global_tokens.first) ],
        [ "truncated masked token", global_tokens.first[0..-5] ],
        [ "invalid base64", "!!!!" ],
        [ "empty", "" ]
      ] + form_tokens.map { |f| [ "per-form token #{f["method"]} #{f["action"]}", f["token"] ] }

      requests = [
        [ "/session", "POST" ], [ "/session/", "POST" ], [ "/session", "PUT" ], [ "/rooms/1/messages", "POST" ],
        [ "/rooms/1", "PATCH" ], [ "/rooms/1", "DELETE" ], [ "/rooms/1/involvement", "PUT" ], [ "/account/edit", "POST" ], [ "/other", "POST" ]
      ]

      validity = submitted.flat_map do |label, token|
        requests.map do |path, method|
          c = csrf_controller(CSRF_SESSION_TOKEN, path: path, method: method)
          { "case" => label, "token" => token, "path" => path, "method" => method,
            "expected" => c.send(:valid_authenticity_token?, c.session, token) }
        end
      end

      origins = [
        [ nil, "http://#{HOST}" ], [ "http://#{HOST}", "http://#{HOST}" ], [ "https://#{HOST}", "http://#{HOST}" ],
        [ "http://evil.test", "http://#{HOST}" ], [ "null", "http://#{HOST}" ], [ "http://#{HOST}:8080", "http://#{HOST}" ],
        [ "http://#{HOST}:8080", "http://#{HOST}:8080" ], [ "HTTP://#{HOST.upcase}", "http://#{HOST}" ]
      ].map do |origin, base_url|
        uri = URI(base_url)
        env = { "HTTP_HOST" => uri.port == uri.default_port ? uri.host : "#{uri.host}:#{uri.port}", "rack.url_scheme" => uri.scheme }
        env["HTTP_ORIGIN"] = origin if origin
        request = request_for(env: env)
        controller = ApplicationController.new.tap { |c| c.set_request!(request) }
        expected = begin
          controller.send(:valid_request_origin?)
        rescue ActionController::InvalidAuthenticityToken
          "raises"
        end
        { "origin" => origin, "base_url" => request.base_url, "expected" => expected }
      end

      {
        "session_token" => CSRF_SESSION_TOKEN,
        "global_token_hex" => global_hmac.unpack1("H*"),
        "global_tokens" => global_tokens,
        "form_tokens" => form_tokens,
        "validity" => validity,
        "origin" => origins,
        "per_form_csrf_tokens" => ApplicationController.per_form_csrf_tokens,
        "forgery_protection_origin_check" => ApplicationController.forgery_protection_origin_check,
        "generated_session_token_example" => SecureRandom.urlsafe_base64(32)
      }
    end

    def xor(a, b)
      a.bytes.zip(b.bytes).map { |x, y| x ^ y }.pack("C*")
    end

    # --- Signed ids -----------------------------------------------------------------------------

    def signed_id_vectors
      users = [ @david, @jason, User.instantiate("id" => 12345), User.instantiate("id" => 9_007_199_254_740_993) ]
      generate = users.flat_map do |user|
        [
          { "model" => "User", "id" => user.id, "purpose" => "avatar", "expires_at" => nil, "signed_id" => user.signed_id(purpose: :avatar) },
          { "model" => "User", "id" => user.id, "purpose" => "transfer", "expires_at" => iso(4.hours.from_now), "signed_id" => user.transfer_id },
          { "model" => "User", "id" => user.id, "purpose" => nil, "expires_at" => nil, "signed_id" => user.signed_id }
        ]
      end
      generate << { "model" => "Room", "id" => @room.id, "purpose" => "avatar", "expires_at" => nil, "signed_id" => @room.signed_id(purpose: :avatar) }
      generate << { "model" => "User", "id" => @david.id, "purpose" => "transfer", "expires_at" => iso(NOW + 30.minutes), "signed_id" => @david.signed_id(purpose: :transfer, expires_at: NOW + 30.minutes) }

      avatar = generate.first
      transfer = generate[1]
      rotated_verifier = ActiveSupport::MessageVerifier.new(rotated_key_generator.generate_key("active_record/signed_id"), digest: "SHA256", serializer: JSON, url_safe: true)
      fallback_verifier = User.signed_id_verifier.instance_variable_get(:@rotations).first

      cases = generate.map { |g| [ "valid #{g["model"]} #{g["id"]} #{g["purpose"]}", g["model"], g["signed_id"], g["purpose"], NOW ] }
      cases += [
        [ "transfer before expiry", "User", transfer["signed_id"], "transfer", NOW + 4.hours - 1.second ],
        [ "transfer at expiry", "User", transfer["signed_id"], "transfer", NOW + 4.hours ],
        [ "transfer after expiry", "User", transfer["signed_id"], "transfer", NOW + 5.hours ],
        [ "avatar far in the future", "User", avatar["signed_id"], "avatar", NOW + 50.years ],
        [ "avatar id used as transfer", "User", avatar["signed_id"], "transfer", NOW ],
        [ "avatar id without purpose", "User", avatar["signed_id"], nil, NOW ],
        [ "room avatar id verified as user", "User", generate.find { |g| g["model"] == "Room" }["signed_id"], "avatar", NOW ],
        [ "user avatar id verified as room", "Room", avatar["signed_id"], "avatar", NOW ],
        [ "tampered digest", "User", tamper_last(avatar["signed_id"]), "avatar", NOW ],
        [ "tampered payload", "User", tamper_first(avatar["signed_id"]), "avatar", NOW ],
        [ "signed with rotated secret", "User", rotated_verifier.generate(@david.id, purpose: "user/avatar"), "avatar", NOW ],
        [ "garbage", "User", "garbage", "avatar", NOW ],
        [ "empty", "User", "", "avatar", NOW ],
        [ "legacy fallback verifier (SHA1, standard base64)", "User", fallback_verifier.generate(@david.id, purpose: "user/avatar"), "avatar", NOW ],
        [ "legacy fallback verifier, wrong purpose", "User", fallback_verifier.generate(@david.id, purpose: "user/transfer"), "avatar", NOW ],
        [ "standard base64 of url-safe message", "User", resign_standard_base64(avatar["signed_id"]), "avatar", NOW ],
        [ "string id", "User", User.signed_id_verifier.generate("7", purpose: "user/avatar"), "avatar", NOW ]
      ]

      verify = cases.map do |label, model, signed_id, purpose, now|
        klass = model.constantize
        id = at(now) { klass.signed_id_verifier.verified(signed_id, purpose: klass.combine_signed_id_purposes(purpose)) }
        { "case" => label, "model" => model, "signed_id" => signed_id, "purpose" => purpose, "now" => iso(now), "expected" => id }
      end

      { "generate" => generate, "verify" => verify }
    end

    # The same payload, encoded with the standard alphabet and padding, and re-signed.
    def resign_standard_base64(signed_id)
      data = signed_id.split("--").first
      standard = Base64.strict_encode64(Base64.urlsafe_decode64(data))
      "#{standard}--#{OpenSSL::HMAC.hexdigest("SHA256", app.key_generator.generate_key("active_record/signed_id"), standard)}"
    end

    # --- Global ids -----------------------------------------------------------------------------

    def global_id_vectors
      [ @david, @jason, @room, Account.first ].map do |record|
        { "model_name" => record.class.name, "id" => record.id.to_s, "gid" => record.to_gid.to_s, "param" => record.to_gid_param }
      end
    end

    # --- Signed global ids ----------------------------------------------------------------------

    def sgid_verifier(secret: app.key_generator.generate_key("signed_global_ids"), **options)
      GlobalID::Verifier.new(secret, **options)
    end

    # Rails 7.0: Marshal serializer and the "message" envelope (data dumped separately).
    def marshal_era_sgid(gid, purpose, secret: app.key_generator.generate_key("signed_global_ids"), expires_at: nil)
      sgid_verifier(secret: secret, serializer: :marshal, force_legacy_metadata_serializer: true)
        .generate(gid, purpose: purpose, expires_at: expires_at)
    end

    # Rails 7.1 with JSON serializer but the legacy metadata envelope.
    def json_legacy_envelope_sgid(gid, purpose)
      sgid_verifier(serializer: :json, force_legacy_metadata_serializer: true).generate(gid, purpose: purpose)
    end

    # globalid < 1.0: the metadata lived in the payload, not in a Rails envelope.
    def self_validated_sgid(gid, purpose, expires_at)
      sgid_verifier.generate({ "gid" => gid, "purpose" => purpose, "expires_at" => expires_at })
    end

    def sgid_vectors
      generate = [ @david, @jason ].map do |user|
        # attachable_sgid calls to_sgid(expires_in: nil, for: "attachable"), and GlobalID.create turns the
        # leftover expires_in option into a query param: the signed data is "gid://campfire/User/1?expires_in".
        sgid = user.attachable_sgid
        { "gid" => user.to_gid.to_s, "data" => SignedGlobalID.parse(sgid, for: "attachable").uri.to_s, "purpose" => "attachable", "expires_at" => nil, "sgid" => sgid }
      end
      generate << { "gid" => @room.to_gid.to_s, "data" => "#{@room.to_gid}?expires_in", "purpose" => "attachable", "expires_at" => nil, "sgid" => @room.to_sgid(expires_in: nil, for: "attachable").to_s }
      generate << { "gid" => @david.to_gid.to_s, "data" => @david.to_gid.to_s, "purpose" => "default", "expires_at" => iso(1.month.from_now), "sgid" => @david.to_sgid.to_s }
      generate << { "gid" => @david.to_gid.to_s, "data" => @david.to_gid.to_s, "purpose" => "attachable", "expires_at" => iso(1.hour.from_now),
        "sgid" => SignedGlobalID.new(@david.to_gid.to_s, for: "attachable", expires_at: 1.hour.from_now).to_s }

      david = generate.first
      short = generate.last
      rotated_secret = rotated_key_generator.generate_key("signed_global_ids")
      gid = @david.to_gid.to_s

      cases = generate.map { |g| [ "valid #{g["gid"]} for #{g["purpose"]}", g["sgid"], g["purpose"], NOW ] }
      cases += [
        [ "wrong purpose", david["sgid"], "default", NOW ],
        [ "attachable sgid far in the future", david["sgid"], "attachable", NOW + 50.years ],
        [ "short-lived before expiry", short["sgid"], "attachable", NOW + 59.minutes ],
        [ "short-lived after expiry", short["sgid"], "attachable", NOW + 2.hours ],
        [ "default purpose after a month", generate[3]["sgid"], "default", NOW + 1.month + 1.second ],
        [ "tampered digest", tamper_last(david["sgid"]), "attachable", NOW ],
        [ "tampered payload", tamper_first(david["sgid"]), "attachable", NOW ],
        [ "signed with rotated secret", sgid_verifier(secret: rotated_secret).generate(gid, purpose: "attachable"), "attachable", NOW ],
        [ "garbage", "garbage", "attachable", NOW ],
        [ "marshal era (Rails 7.0)", marshal_era_sgid(gid, "attachable"), "attachable", NOW ],
        [ "marshal era with expires_in param", marshal_era_sgid("#{gid}?expires_in", "attachable"), "attachable", NOW ],
        [ "marshal era, wrong purpose", marshal_era_sgid(gid, "default"), "attachable", NOW ],
        [ "marshal era, expired", marshal_era_sgid(gid, "attachable", expires_at: NOW - 1.minute), "attachable", NOW ],
        [ "json legacy envelope", json_legacy_envelope_sgid(gid, "attachable"), "attachable", NOW ],
        [ "self-validated metadata (globalid < 1.0)", self_validated_sgid(gid, "attachable", nil), "attachable", NOW ],
        [ "self-validated metadata, wrong purpose", self_validated_sgid(gid, "default", nil), "attachable", NOW ],
        [ "self-validated metadata, expired", self_validated_sgid(gid, "attachable", iso(NOW - 1.minute)), "attachable", NOW ],
        [ "self-validated metadata, not yet expired", self_validated_sgid(gid, "attachable", iso(NOW + 1.minute)), "attachable", NOW ],
        [ "unpadded url-safe base64", unpadded_resigned(david["sgid"]), "attachable", NOW ]
      ]

      verify = cases.map do |label, sgid, purpose, now|
        located = at(now) { SignedGlobalID.parse(sgid, for: purpose)&.uri&.to_s }
        { "case" => label, "sgid" => sgid, "purpose" => purpose, "now" => iso(now), "expected" => located }
      end

      { "app" => GlobalID.app, "generate" => generate, "verify" => verify }
    end

    def unpadded_resigned(sgid)
      data = sgid.split("--").first.delete("=")
      "#{data}--#{OpenSSL::HMAC.hexdigest("SHA1", app.key_generator.generate_key("signed_global_ids"), data)}"
    end

    # lib/rails_ext/action_text_attachables.rb: signatures are ignored, but only User comes back.
    def unverified_sgid_vectors
      rotated_secret = rotated_key_generator.generate_key("signed_global_ids")
      rotated = ->(gid, purpose = "attachable") { sgid_verifier(secret: rotated_secret).generate(gid, purpose: purpose) }
      missing_user = "gid://campfire/User/999"

      cases = [
        [ "valid user sgid", @david.attachable_sgid ],
        [ "user sgid signed with rotated secret", rotated.(@jason.to_gid.to_s) ],
        [ "user sgid with another purpose", rotated.(@jason.to_gid.to_s, "default") ],
        [ "tampered user sgid", tamper_last(@david.attachable_sgid) ],
        [ "marshal era user sgid, rotated secret", marshal_era_sgid(@david.to_gid.to_s, "attachable", secret: rotated_secret) ],
        [ "marshal era user sgid with params, rotated secret", marshal_era_sgid("#{@jason.to_gid}?expires_in", "attachable", secret: rotated_secret) ],
        [ "marshal era user sgid, marshal with a long string", marshal_era_sgid("#{@jason.to_gid}?#{"x" * 300}", "attachable", secret: rotated_secret) ],
        [ "marshal era room sgid, rotated secret", marshal_era_sgid(@room.to_gid.to_s, "attachable", secret: rotated_secret) ],
        [ "marshal era gid of another app", marshal_era_sgid("gid://other/User/1", "attachable", secret: rotated_secret) ],
        [ "room sgid signed with rotated secret", rotated.(@room.to_gid.to_s) ],
        [ "account sgid signed with rotated secret", rotated.(Account.first.to_gid.to_s) ],
        [ "missing user", rotated.(missing_user) ],
        [ "user gid of another app", rotated.("gid://other/User/#{@david.id}") ],
        [ "no signature part", @david.attachable_sgid.split("--").first ],
        [ "standard base64 payload", Base64.strict_encode64(ActiveSupport::JSON.encode({ "_rails" => { "data" => @david.to_gid.to_s, "pur" => "attachable" } })) + "--x" ],
        [ "envelope without data or message", Base64.strict_encode64(%({"_rails":{"pur":"attachable"}})) + "--x" ],
        [ "not an envelope", Base64.strict_encode64(%({"gid":"#{@david.to_gid}"})) + "--x" ],
        [ "not a gid", Base64.strict_encode64(%({"_rails":{"data":"hello"}})) + "--x" ],
        [ "nil", nil ],
        [ "empty", "" ],
        [ "not json", Base64.strict_encode64("hello") + "--x" ],
        [ "not base64", "!!!--x" ],
        [ "json array", Base64.strict_encode64("[1]") + "--x" ]
      ]

      cases.map do |label, sgid|
        expected = begin
          ActionText::Attachment.send(:attachable_from_possibly_expired_sgid, sgid)&.to_gid&.to_s
        rescue => error
          { "raises" => error.class.name }
        end
        { "case" => label, "sgid" => sgid, "expected" => expected }
      end
    end

    # --- Turbo signed stream names --------------------------------------------------------------

    def turbo_vectors
      streamables = [
        [ [ @room, :messages ], [ @room.to_gid_param, "messages" ] ],
        [ :rooms, [ "rooms" ] ],
        [ [ @david, :rooms ], [ @david.to_gid_param, "rooms" ] ],
        [ @david, [ @david.to_gid_param ] ],
        [ "unicode ☃ <&>", [ "unicode ☃ <&>" ] ],
        [ [ :a, [ :b, :c ] ], [ "a", "b:c" ] ]
      ]

      generate = streamables.map do |streamable, parts|
        signed = Turbo::StreamsChannel.signed_stream_name(streamable)
        { "parts" => parts, "stream_name" => Turbo::StreamsChannel.send(:stream_name_from, streamable), "signed" => signed }
      end

      rotated_verifier = ActiveSupport::MessageVerifier.new(rotated_key_generator.generate_key("turbo/signed_stream_verifier_key"), digest: "SHA256", serializer: JSON)
      first = generate.first["signed"]
      cases = generate.map { |g| [ "valid #{g["stream_name"]}", g["signed"] ] } + [
        [ "tampered digest", tamper_last(first) ],
        [ "tampered payload", tamper_first(first) ],
        [ "signed with rotated secret", rotated_verifier.generate("rooms") ],
        [ "garbage", "garbage" ],
        [ "with purpose metadata", Turbo.signed_stream_verifier.generate("rooms", purpose: "x") ],
        [ "json non-string", Turbo.signed_stream_verifier.generate(42) ]
      ]
      verify = cases.map { |label, signed| { "case" => label, "signed" => signed, "expected" => Turbo::StreamsChannel.verified_stream_name(signed) } }

      { "generate" => generate, "verify" => verify }
    end

    # --- Named app verifiers (Rails.application.message_verifier) -------------------------------

    def app_verifier_vectors
      verifier = ActiveStorage.verifier
      blob_key = { key: "xtapjjcjiudrlk3tmwyjgpuobabd", disposition: "inline; filename=\"a&b.png\"; filename*=UTF-8''a%26b.png",
                   content_type: "image/png", service_name: "local" }
      generate = [
        [ "ActiveStorage", 42, "blob_id", nil ],
        [ "ActiveStorage", 9_007_199_254_740_993, "blob_id", nil ],
        [ "ActiveStorage", blob_key, "blob_key", NOW + 5.minutes ],
        [ "ActiveStorage", { key: "k", content_type: "text/plain", content_length: 12, checksum: "abc==", service_name: "local" }, "blob_token", NOW + 5.minutes ],
        [ "ActiveStorage", "0123abc", "variation", nil ],
        [ "ActiveStorage", "no purpose", nil, nil ],
        [ "something else", { "b" => 1, "a" => [ "<&>" ] }, "x", nil ]
      ].map do |name, data, purpose, expires_at|
        { "name" => name, "data_json" => ActiveSupport::JSON.encode(data), "purpose" => purpose, "expires_at" => iso(expires_at),
          "message" => app.message_verifier(name).generate(data, purpose: purpose, expires_at: expires_at) }
      end
      generate << { "name" => "ActiveStorage", "data_json" => "42", "purpose" => "blob_id", "expires_at" => nil,
                    "message" => ActiveStorage::Blob.instantiate("id" => 42, "service_name" => "local").signed_id, "via" => "ActiveStorage::Blob#signed_id" }

      blob = generate.first["message"]
      short = generate[2]["message"]
      rotated = ActiveSupport::MessageVerifier.new(rotated_key_generator.generate_key("ActiveStorage"), serializer: :json_allow_marshal)
      cases = generate.map { |g| [ "valid #{g["name"]} #{g["purpose"]}", g["name"], g["message"], g["purpose"], NOW ] } + [
        [ "wrong purpose", "ActiveStorage", blob, "blob_key", NOW ],
        [ "wrong verifier name", "ActionText", blob, "blob_id", NOW ],
        [ "expired", "ActiveStorage", short, "blob_key", NOW + 5.minutes ],
        [ "before expiry", "ActiveStorage", short, "blob_key", NOW + 4.minutes ],
        [ "tampered", "ActiveStorage", tamper_last(blob), "blob_id", NOW ],
        [ "rotated secret", "ActiveStorage", rotated.generate(42, purpose: "blob_id"), "blob_id", NOW ],
        [ "url-safe encoding", "ActiveStorage", ActiveSupport::MessageVerifier.new(app.key_generator.generate_key("ActiveStorage"), url_safe: true, serializer: :json_allow_marshal).generate("x" * 50 + "?>", purpose: "p"), "p", NOW ]
      ]
      verify = cases.map do |label, name, message, purpose, now|
        value = at(now) { app.message_verifier(name).verified(message, purpose: purpose) }
        { "case" => label, "name" => name, "message" => message, "purpose" => purpose, "now" => iso(now),
          "expected_json" => value.nil? ? nil : ActiveSupport::JSON.encode(value) }
      end

      { "generate" => generate, "verify" => verify }
    end

    # --- Passwords (has_secure_password) --------------------------------------------------------

    def password_vectors
      long = "a" * 72
      passwords = [ "secret123456", "pässwörd ☃", long, long + "ignored", "x" ]
      digests = passwords.map { |password| { "password" => password, "digest" => User.new(password: password).password_digest } }

      checks = digests.flat_map do |d|
        (passwords + [ "wrong", long[0..-2], "secret123456 " ]).uniq.map do |attempt|
          { "digest" => d["digest"], "password" => attempt, "expected" => BCrypt::Password.new(d["digest"]).is_password?(attempt) }
        end
      end

      { "cost" => BCrypt::Engine.cost, "digests" => digests, "checks" => checks, "seeded_user_digest" => @david.password_digest }
    end
end

File.write(File.join(ENV.fetch("VECTORS_DIR"), "rails_compat.json"), JSON.pretty_generate(RailsCompatVectors.new.generate) + "\n")
puts "Wrote #{File.join(ENV.fetch("VECTORS_DIR"), "rails_compat.json")}"
