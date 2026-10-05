# Regenerates surfguard.json from the surfguard gem the reference pins
# (reference/Gemfile.lock: basecamp/surfguard 59e278c, ipaddr 1.2.7 from Ruby 3.4).
#
#   git clone https://github.com/basecamp/surfguard && git -C surfguard checkout 59e278c
#   gem install ipaddr -v 1.2.7
#   SURFGUARD_DIR=surfguard ruby tests/Campfire.Jobs.Tests/RestrictedHttp/Vectors/generate.rb
gem "ipaddr", "1.2.7"
require "json"
require File.join(ENV.fetch("SURFGUARD_DIR"), "lib/surfguard")

raise "surfguard 0.2.0 expected" unless Surfguard::VERSION == "0.2.0"
raise "ipaddr 1.2.7 expected" unless IPAddr::VERSION == "1.2.7"

DNS_ANSWER = "93.184.216.34"
conformance = File.join(ENV.fetch("SURFGUARD_DIR"), "conformance")

def conformance_inputs(dir)
  Dir[File.join(dir, "*.json")].sort.flat_map do |path|
    JSON.parse(File.read(path)).fetch("cases").map { |kase| kase.fetch("input") }
  end
end

reference_test_inputs = %w[
  0.0.0.0 0.255.255.255 127.0.0.0 127.0.0.1 127.255.255.255 10.0.0.0 10.255.255.255 172.16.0.0
  172.31.255.255 192.168.0.0 192.168.255.255 169.254.0.1 169.254.169.254 169.254.255.255
  93.184.216.34 8.8.8.8 ::ffff:192.168.1.1 ::ffff:10.0.0.1 ::ffff:172.16.0.1 ::ffff:169.254.169.254
  ::ffff:93.184.216.34 ::192.168.1.1 ::10.0.0.1 ::169.254.169.254 ::93.184.216.34 100.64.0.1
  100.127.255.255 64:ff9b::a9fe:a9fe 64:ff9b::a00:5 64:ff9b:1::a00:1 64:ff9b:1::808:808
  64:ff9b:1:ffff::1 ::ffff:0:169.254.169.254 ::ffff:0:a9fe:a9fe ::ffff:0:127.0.0.1
  ::ffff:0:192.168.0.1 64:ff9b::808:808 2002:a9fe:a9fe:: 2001::1 ::1 fd00:ec2::254 fe80::1 ff02::1
  2001:db8::1 2001:2::1 2606:4700:4700::1111 not-an-ip
] + [ "" ]

# Spellings the host parser has to agree on: brackets, prefixes, netmasks, case, legacy forms.
host_spellings = %w[
  [::1] [2606:4700:4700::1111] [::ffff:127.0.0.1] [93.184.216.34] [fe80::1%25eth0] ::1/128 ::1/0128
  2606:4700:4700::1111/128 2606:4700:4700::1111/127 93.184.216.34/32 93.184.216.34/032
  93.184.216.34/255.255.255.255 93.184.216.34/255.255.255.0 93.184.216.34/0.255.255.255 127.0.0.1/32
  ::ffff:7f00:1 ::FFFF:7F00:1 2606:4700:4700:0:0:0:0:1111 2606:4700:4700::1111:: :::1 1:2:3:4:5:6:7:8:9
  ::1.2.3.4.5 1::2::3 2606:4700:4700::1111:93.184.216.34 1:2:3:4:5:6:1.2.3.4 0177.0.0.1 0x7f.1 0x7F.0.0.1
  10.0.258 9.0x7f.0.1 1.2.3.0x100 1.2.3.256 1.2.3.4.5 09.1.1.1 08 0x 0xg 4294967295 4294967296
  01.02.03.04 1.1.1.01 256.256.256.256 example.com EXAMPLE.com xn--bcher-kva.example a.b-.c -a.b
  a_b.example localhost localhost. . .. a..b 1.example 1-2.example 0x1.example 1.2.3.4.example
  host:80 example.com:443 ::1:80 [::1]:80 [::1 ::1] [v1.fe] 1.2.3.4/33 1.2.3.4/-1 1.2.3.4/ /
] + [ "a" * 63 + ".example", "a" * 64 + ".example", ([ "a" * 63 ] * 4).join("."), "1.2.3.4 ", " 1.2.3.4",
  "ex ample.com", "exämple.com", "a\u0000b" ] + [ ("a" * 63 + ".") * 4 + "a" ]

def boundaries(cidr)
  range = IPAddr.new(cidr).to_range
  first, last = range.first, range.last
  bits = first.ipv4? ? 32 : 128
  [ first.to_i - 1, first.to_i, first.to_i + 1, last.to_i - 1, last.to_i, last.to_i + 1 ]
    .select { |n| n >= 0 && n < (1 << bits) }
    .map { |n| IPAddr.new(n, first.family).to_s }
end

prefixes = Surfguard::DISALLOWED_IPV4 + Surfguard::DISALLOWED_IPV6 + Surfguard::IANA_ALLOCATED_IPV6_UNICAST +
  Surfguard::GLOBALLY_REACHABLE_IETF_ASSIGNMENTS +
  [ Surfguard::IETF_PROTOCOL_ASSIGNMENTS, Surfguard::NAT64_WELL_KNOWN, Surfguard::NAT64_LOCAL_USE,
    Surfguard::IPV4_TRANSLATABLE, Surfguard::IPV4_COMPATIBLE ] +
  %w[::ffff:0:0/96 fc00::/7 fe80::/10 ::1/128].map { |cidr| IPAddr.new(cidr) }
boundary_inputs = prefixes.flat_map { |prefix| boundaries("#{prefix}/#{prefix.prefix}") }

# The same addresses wrapped in every IPv4-in-IPv6 encoding.
embedded_inputs = Surfguard::DISALLOWED_IPV4.flat_map { |prefix| boundaries("#{prefix}/#{prefix.prefix}") }
  .select { |ip| IPAddr.new(ip).ipv4? }
  .flat_map { |ip| [ "::ffff:#{ip}", "::#{ip}", "::ffff:0:#{ip}", "64:ff9b::#{ip}", "64:ff9b:1::#{ip}" ] }

random = Random.new(20261005)
random_inputs = Array.new(700) { IPAddr.new(random.rand(1 << 32), Socket::AF_INET).to_s } +
  Array.new(700) { IPAddr.new(random.rand(1 << 128), Socket::AF_INET6).to_s } +
  Array.new(700) do
    # Random addresses inside the prefixes the policy names, so most land somewhere interesting.
    prefix = prefixes[random.rand(prefixes.length)]
    bits = prefix.ipv4? ? 32 : 128
    host_bits = bits - prefix.prefix
    IPAddr.new(prefix.to_i | (host_bits.zero? ? 0 : random.rand(1 << host_bits)), prefix.family).to_s
  end

inputs = (conformance_inputs(conformance) + reference_test_inputs + host_spellings + boundary_inputs +
  embedded_inputs + random_inputs).uniq

def classify(input)
  Surfguard.blocked_address?(input)
end

# What resolve_public_ips does with the host when every DNS lookup answers DNS_ANSWER.
def resolve_host(input)
  dns = false
  Resolv.singleton_class.alias_method(:original_getaddresses, :getaddresses)
  Resolv.define_singleton_method(:getaddresses) { |_| dns = true; [ DNS_ANSWER ] }
  ips = Surfguard.resolve_public_ips(input)
  { "dns" => dns, "ips" => ips }
rescue Surfguard::Unresolvable
  { "dns" => dns, "error" => "unresolvable" }
rescue => e
  { "dns" => dns, "error" => e.class.name }
ensure
  Resolv.singleton_class.alias_method(:getaddresses, :original_getaddresses)
end

def resolve_answers(answers)
  Resolv.singleton_class.alias_method(:original_getaddresses, :getaddresses)
  Resolv.define_singleton_method(:getaddresses) { |_| answers }
  { "ips" => Surfguard.resolve_public_ips("example.com") }
rescue Surfguard::Unresolvable
  { "error" => "unresolvable" }
ensure
  Resolv.singleton_class.alias_method(:getaddresses, :original_getaddresses)
end

answer_cases = [
  [], [ "93.184.216.34" ], [ "192.168.1.1" ], [ "127.0.0.1" ],
  [ "93.184.216.34", "127.0.0.1" ], [ "127.0.0.1", "93.184.216.34" ],
  [ "2606:4700:4700::1111", "93.184.216.34" ], [ "2606:4700:4700::1111", "::1", "8.8.8.8", "10.0.0.1", "1.1.1.1" ],
  [ "::ffff:93.184.216.34" ], [ "::ffff:93.184.216.34", "93.184.216.34" ], [ "169.254.169.254", "fd00:ec2::254" ],
  [ "93.184.216.34", "93.184.216.34", "8.8.8.8", "93.184.216.34" ], [ "64:ff9b::808:808", "64:ff9b::a00:1" ],
  Array.new(256) { |i| "93.184.#{i / 256}.#{i % 256}" }, Array.new(257) { |i| "93.184.#{i / 256}.#{i % 256}" },
  Array.new(300) { "93.184.216.34" }
].map { |answers| { "answers" => answers }.merge(resolve_answers(answers)) }

vectors = {
  "source" => "basecamp/surfguard@#{`git -C #{ENV.fetch("SURFGUARD_DIR")} rev-parse --short HEAD`.strip} " \
    "(surfguard #{Surfguard::VERSION}, ipaddr #{IPAddr::VERSION}, ruby #{RUBY_VERSION})",
  "dns_answer" => DNS_ANSWER,
  "hosts" => inputs.map { |input| { "input" => input, "blocked" => classify(input) }.merge(resolve_host(input)) },
  "answers" => answer_cases
}

File.write(File.join(__dir__, "surfguard.json"), JSON.pretty_generate(vectors) + "\n")
puts "#{vectors["hosts"].length} hosts, #{answer_cases.length} answer cases"
