# A signed-in integration session against the reference app, for rendering real responses.
require_relative "prelude"

module ViewsB
  HOST = "campfire.test"

  def self.session_for(user)
    s = ActionDispatch::Integration::Session.new(Rails.application)
    s.host! HOST
    s.https!(false)
    return s if user.bot?
    s.get "/session/new"
    token = s.response.body[/name="csrf-token" content="([^"]+)"/, 1]
    s.post "/session", params: { email_address: user.email_address, password: "secret123456", authenticity_token: token }
    raise "sign in failed for #{user.name}" unless s.response.redirect?
    s
  end

  # Fetches a fresh authenticity token for non-GET requests from any page with csrf meta tags.
  def self.csrf_token(s)
    s.get "/searches"
    s.response.body[/name="csrf-token" content="([^"]+)"/, 1]
  end
end
