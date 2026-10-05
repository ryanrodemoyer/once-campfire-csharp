# Golden vectors for the Rust port of rqrcode (crates/campfire/src/controllers/qr_code/rqrcode.rs):
# QrCodeController#show renders `RQRCode::QRCode.new(url).as_svg(viewbox: true, fill: :white, color: :black)`.
#
#   docker run --rm -v "$PWD:/work" -w /rails campfire-reference \
#     ruby /work/reference-tools/campfire/rqrcode.rb > crates/campfire/src/controllers/qr_code/testdata/rqrcode.json
require "bundler/setup"
require "rqrcode"
require "json"
require "base64"

signed = "eyJfcmFpbHMiOnsiZGF0YSI6MSwiZXhwIjoiMjAyNi0wMy0wMlQyMDowMDowMC4wMDBaIiwicHVyIjoiVXNlclxudHJhbnNmZXJcbjE0NDAwIn19--6f1c2d3e4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c9d"
inputs = [
  "",
  "a",
  "0",
  "12345",
  "0123456789",
  "31415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679",
  "HELLO WORLD",
  "HTTP://CAMPFIRE.TEST/JOIN/ABCD-EFGH-IJKL",
  "ABC$%*+-./: 123",
  "http://campfire.test",
  "https://campfire.test/",
  "http://campfire.test/join/aB3d-Ef6H-iJ9k",
  "https://chat.example.com/join/Zz09-AbCd-XyZ1",
  "http://campfire.test/session/transfers/#{signed}",
  "https://a-rather-long-hostname.example.co.uk:8443/session/transfers/#{signed}",
  "http://campfire.test/rooms/1/@42",
  "héllo wörld",
  "日本語のテキスト",
  "emoji 🔥🔥🔥",
  "Mixed Case 123 with spaces",
  "x" * 50,
  "x" * 100,
  "x" * 150,
  "x" * 200,
  "y" * 250,
  "z" * 300,
  "https://campfire.test/" + ("abcdefghij" * 32),
  "9" * 400,
  "A" * 500,
  (0..255).map(&:chr).join.b[32, 90],
]
inputs += (1..8).map { |i| "https://campfire.test/rooms/#{i * 37}/messages/#{i * 1013}?page=#{i}" }

# The full SVG for the first few inputs; the module matrix (rows of 0/1) for all of them, since the
# SVG is a direct function of it and would make the file huge.
vector = ->(input, full_svg) do
  qr = RQRCode::QRCode.new(input)
  { input_base64: Base64.strict_encode64(input.b), version: qr.qrcode.version,
    modules: qr.qrcode.to_s(dark: "1", light: "0"),
    svg: (qr.as_svg(viewbox: true, fill: :white, color: :black) if full_svg) }.compact
end
vectors = inputs.each_with_index.map { |input, i| vector.(input, i < 4) }

# The controller decodes Base64.urlsafe_decode64(params[:id]) into a binary string.
url = "http://campfire.test/session/transfers/#{signed}"
decoded = Base64.urlsafe_decode64(Base64.urlsafe_encode64(url))
vectors << vector.(decoded, true)

puts JSON.pretty_generate(vectors)
