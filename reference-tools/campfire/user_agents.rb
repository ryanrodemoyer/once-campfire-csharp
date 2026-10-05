# Golden vectors for the Rust ports of the useragent gem (crates/campfire/src/concerns/user_agent.rs),
# ApplicationPlatform and AllowBrowser's BrowserBlocker (crates/campfire/src/concerns/platform.rs).
#
#   parity/bin/reference runner reference-tools/campfire/user_agents.rb > vectors/campfire_user_agents.json
#
# A value that raises in Ruby is recorded as { "error": "<exception class>" }.
require "json"
require "yaml"

def rb
  yield
rescue => e
  { "error" => e.class.name }
end

# The seed's user agents: parity/seeds/ once this repository has its own, else the Rust port's.
work = ENV.fetch("PARITY_WORK", Dir.pwd)
seeds_file = [ "parity/seeds/user_agents.yml", "reference-rust/parity/seeds/user_agents.yml" ]
  .map { |path| File.join(work, path) }.find { |path| File.exist?(path) }
seeds = YAML.load_file(seeds_file).values

corpus = seeds + [
  nil, "", "   ", "\t",

  # Chrome
  "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/49.0.2623.112 Safari/537.36",
  "Mozilla/5.0 (Windows NT 6.3; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/109.0.0.0 Safari/537.36",
  "Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/49.0.2623.112 Safari/537.36",
  "Mozilla/5.0 (Windows NT 5.1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/49.0.2623.112 Safari/537.36",
  "Mozilla/5.0 (Windows NT 6.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/49.0.2623.112 Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.6045.199 Safari/537.36",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.6099.71 Safari/537.36",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_9_5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/60.0.3112.113 Safari/537.36",
  "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
  "Mozilla/5.0 (X11; CrOS armv7l 13597.84.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.186 Safari/537.36",
  "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
  "Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/119.0.6045.169 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/121.0.6167.66 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Iron/120.0.6050.0 Chrome/120.0.6050.0 Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36",
  "Mozilla/5.0 (Linux; Android 11; moto g power (2022)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Mobile Safari/537.36 Chrome-Lighthouse",
  "Mozilla/5.0 (Linux; Android 13; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (X11; U; Linux i686; en-US) AppleWebKit/534.3 (KHTML, like Gecko) Chrome/6.0.472.63 Safari/534.3",
  "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0,gzip(gfe)",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Ubuntu Chromium/37.0.2062.94 Chrome/37.0.2062.94 Safari/537.36",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 YaBrowser/24.1.0.0 Safari/537.36",
  "Mozilla/5.0 (Linux; U; Android 8.1.0; en-US; Nexus 6P Build/OPM7.181205.001) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/57.0.2987.108 UCBrowser/12.11.1.1197 Mobile Safari/537.36",
  "Chrome/100.0",
  "Chrome/120.0.0.0.0.0.1",
  "Chrome/119.9.9.9.9.9.9",
  "Chrome/0120",
  "Chrome/99999999999999999999999",
  "Chrome/",
  "Chrome",
  "chrome/200",
  "CHROME/5",

  # Edge
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19582",
  "Mozilla/5.0 (Windows Phone 10.0; Android 6.0.1; Microsoft; Lumia 950) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/52.0.2743.116 Mobile Safari/537.36 Edge/15.15063",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edge/124.0",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.2478.51",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.2478.51",
  "Mozilla/5.0 (Linux; Android 10; HD1913) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.54 Mobile Safari/537.36 EdgA/124.0.2478.50",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 EdgiOS/124.2478.50 Mobile/15E148 Safari/605.1.15",
  "Edge/12.0",

  # Opera
  "Opera/9.80 (Windows NT 6.1; U; en) Presto/2.9.168 Version/11.52",
  "Opera/9.80 (Macintosh; Intel Mac OS X 10.6.8; U; en) Presto/2.9.168 Version/11.52",
  "Opera/9.80 (J2ME/MIDP; Opera Mini/9.80 (S60; SymbOS; Opera Mobi/23.348; U; en) Presto/2.5.25 Version/10.54",
  "Opera/9.80 (Android; Opera Mini/7.5.33361/31.1448; U; en) Presto/2.8.119 Version/11.1010",
  "Opera/9.80 (Android; Opera Mini; U; en) Presto/2.8.119",
  "Opera/9.64 (X11; Linux i686; U; en) Presto/2.1.1",
  "Opera/8.0 (Windows NT 5.1; U; en)",
  "Opera/9.80 (Windows NT 6.0) Presto/2.12.388 Version/12.14",
  "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; en) Opera 8.50",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.4896.127 Safari/537.36 OPR/86.0.4363.59",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36 OPR/104.0.0.0",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36 OPR/103.0.0.0",
  "Mozilla/5.0 (Linux; Android 10; VOG-L29) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.181 Mobile Safari/537.36 OPR/63.3.3216.58675",
  "Opera",
  "Opera/9",

  # Vivaldi
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Vivaldi/6.5.3206.53",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Vivaldi/6.5.3206.53",
  "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Vivaldi/6.5",

  # Internet Explorer
  "Mozilla/2.0 (compatible; MSIE 3.02; Windows 95)",
  "Mozilla/4.0 (compatible; MSIE 5.5; Windows 98)",
  "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1)",
  "Mozilla/4.0 (compatible; MSIE 6.0; Windows CE; IEMobile 7.11)",
  "Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 6.0)",
  "Mozilla/4.0 (compatible; MSIE 7.0; Windows NT 6.1; Trident/7.0; SLCC2)",
  "Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 6.1; Trident/4.0)",
  "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; Trident/5.0)",
  "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; Trident/6.0)",
  "Mozilla/5.0 (Windows NT 6.3; Trident/7.0; rv:11.0) like Gecko",
  "Mozilla/5.0 (Windows NT 10.0; WOW64; Trident/7.0; rv:11.0) like Gecko",
  "Mozilla/5.0 (compatible; MSIE 10.0; Windows Phone 8.0; Trident/6.0; IEMobile/10.0; ARM; Touch; NOKIA; Lumia 920)",
  "Mozilla/5.0 (Mobile; Windows Phone 8.1; Android 4.0; ARM; Trident/7.0; Touch; rv:11.0; IEMobile/11.0; NOKIA; Lumia 635) like iPhone OS 7_0_3 Mac OS X AppleWebKit/537 (KHTML, like Gecko) Mobile Safari/537",
  "Mozilla/4.0 (compatible; MSIE; Windows NT 6.1)",

  # Firefox and other Gecko browsers
  "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
  "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0",
  "Mozilla/5.0 (X11; Fedora; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
  "Mozilla/5.0 (X11; FreeBSD amd64; rv:121.0) Gecko/20100101 Firefox/121.0",
  "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:40.0) Gecko/20100101 Firefox/40.1",
  "Mozilla/5.0 (Windows NT 5.0; rv:10.0) Gecko/20100101 Firefox/10.0",
  "Mozilla/5.0 (Windows NT 5.01; rv:10.0) Gecko/20100101 Firefox/10.0",
  "Mozilla/5.0 (Windows NT 5.2; rv:10.0) Gecko/20100101 Firefox/10.0",
  "Mozilla/5.0 (Windows NT 4.0; rv:10.0) Gecko/20100101 Firefox/10.0",
  "Mozilla/5.0 (Windows; U; Windows NT 5.1; en-US; rv:1.8.1.20) Gecko/20081217 Firefox/2.0.0.20",
  "Mozilla/5.0 (Windows; U; Win98; en-US; rv:1.8.1) Gecko/20061010 Firefox/2.0",
  "Mozilla/5.0 (Macintosh; U; Intel Mac OS X 10.5; en-US; rv:1.9.0.3) Gecko/2008092414 Firefox/3.0.3",
  "Mozilla/5.0 (Macintosh; U; PPC Mac OS X 10.4; en-US; rv:1.8.1.14) Gecko/20080404 Camino/1.6.1",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.4; rv:121.0) Gecko/20100101 Firefox/121.0",
  "Mozilla/5.0 (Mobile; rv:26.0) Gecko/26.0 Firefox/26.0",
  "Mozilla/5.0 (Android 4.4; Tablet; rv:41.0) Gecko/41.0 Firefox/41.0",
  "Mozilla/5.0 (Android 14; Mobile; rv:121.0) Gecko/121.0 Firefox/121.0",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/124.0 Mobile/15E148 Safari/605.1.15",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:102.0) Gecko/20100101 Goanna/6.5 Firefox/102.0 PaleMoon/32.5.0",
  "Mozilla/5.0 (X11; Linux x86_64; rv:31.0) Gecko/20100101 Iceweasel/31.8.0",
  "Mozilla/5.0 (Windows NT 6.1; rv:2.0.1) Gecko/20100101 SeaMonkey/2.1",
  "Mozilla/5.0 (X11; Linux x86_64; rv:10.0) Gecko/20100101 Firefox",
  "Mozilla/5.0 (compatible; Konqueror/4.5; FreeBSD) KHTML/4.5.4 (like Gecko)",
  "Mozilla/4.0 (compatible)",
  "Mozilla/5.0",
  "Mozilla/5.0 ()",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
  "Mozilla/5.0 (Windows 98; rv:1.0)",
  "Mozilla/5.0 (Windows 95; rv:1.0)",
  "Mozilla/5.0 (Windows CE; rv:1.0)",
  "Mozilla/5.0 (U; Linux)",
  "Firefox/100",

  # Safari and other WebKit browsers
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2.1 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/abc Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2beta Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
  "Mozilla/5.0 (iPad; CPU OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPad; CPU OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPod touch; CPU iPhone OS 12_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/12.1.2 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 16_1_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/450.0.0.38.108;FBBV/569485613]",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Hotwire Native iOS; Turbo Native iOS",
  "Mozilla/5.0 (iPhone; U; CPU iPhone OS 4_3_3 like Mac OS X; en-us) AppleWebKit/533.17.9 (KHTML, like Gecko) Version/5.0.2 Mobile/8J2 Safari/6533.18.5",
  "Mozilla/5.0 (Macintosh; U; PPC Mac OS X; en) AppleWebKit/125.2 (KHTML, like Gecko) Safari/125.8",
  "Mozilla/5.0 (Macintosh; U; PPC Mac OS X; en) AppleWebKit/418.8 (KHTML, like Gecko) Safari/419.3",
  "Mozilla/5.0 (Macintosh; U; Intel Mac OS X 10_7_3; en) AppleWebKit/534.52.7 (KHTML, like Gecko) Safari/534.52.7",
  "Mozilla/5.0 (Windows; U; Windows NT 6.1; en-US) AppleWebKit/534.57.2 (KHTML, like Gecko) Version/5.1.7 Safari/534.57.2",
  "Mozilla/5.0 (Linux; U; Android 4.0.3; ko-kr; LG-L160L Build/IML74K) AppleWebkit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30",
  "Mozilla/5.0 (Linux; U; Android 2.2; en-us; Nexus One Build/FRF91) AppleWebKit/533.1 (KHTML, like Gecko) Version/4.0 Mobile Safari/533.1",
  "Mozilla/5.0 (Linux; Android 4.4.2; Nexus 5) AppleWebKit/537.36 (KHTML, like Gecko) Mobile Safari/537.36",
  "Mozilla/5.0 (BB10; Touch) AppleWebKit/537.10+ (KHTML, like Gecko) Version/10.0.9.2372 Mobile Safari/537.10+",
  "Mozilla/5.0 (BlackBerry; U; BlackBerry 9900; en) AppleWebKit/534.11+ (KHTML, like Gecko) Version/7.1.0.346 Mobile Safari/534.11+",
  "BlackBerry9700/5.0.0.862 Profile/MIDP-2.1 Configuration/CLDC-1.1 VendorID/331 UNTRUSTED/1.0 3gpp-gba",
  "Mozilla/5.0 (Linux; U; en-US) AppleWebKit/528.5+ (KHTML, like Gecko, Safari/528.5+) Version/4.0 Kindle/3.0 (screen 600x800; rotate)",
  "Mozilla/5.0 (Linux; U; Android 4.0.3; en-us; KFTT Build/IML74K) AppleWebKit/535.19 (KHTML, like Gecko) Silk/3.21 Safari/535.19 Silk-Accelerated=true",
  "Mozilla/5.0 (Linux; Android 9; KFTRWI) AppleWebKit/537.36 (KHTML, like Gecko) Silk/120.4.1 like Chrome/120.0.6099.230 Safari/537.36",
  "Mozilla/5.0 (Symbian/3; Series60/5.2 NokiaN8-00/012.002; Profile/MIDP-2.1 Configuration/CLDC-1.1 ) AppleWebKit/533.4 (KHTML, like Gecko) NokiaBrowser/7.3.0 Mobile Safari/533.4 3gpp-gba",
  "Something/1.0 (AppleWebKit/533.1; foo)",
  "Something/1.0 (applewebkit/85.7; foo)",
  "AppleWebKit/537.36",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_11_1) AppleWebKit/601.2.4 (KHTML, like Gecko) Version/9.0.1 Safari/601.2.4 facebookexternalhit/1.1 Facebot Twitterbot/1.0",
  "Safari/1.0",

  # WeChat
  "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 MicroMessenger/8.0.42(0x18002a2c) NetType/WIFI Language/zh_CN",
  "Mozilla/5.0 (Linux; Android 13; V2185A Build/TP1A.220624.014; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/107.0.5304.141 Mobile Safari/537.36 XWEB/5317 MMWEBSDK/20230701 MMWEBID/2350 MicroMessenger/8.0.40.2420(0x28002837) WeChat/arm64 Weixin NetType/WIFI Language/zh_CN ABI/arm64",
  "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/53.0.2785.116 Safari/537.36 QBCore/4.0.1301.400 QQBrowser/9.0.2524.400 Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/53.0.2875.116 Safari/537.36 NetType/WIFI MicroMessenger/7.0.20.1781(0x6700143B) WindowsWechat",
  "micromessenger/1.0",
  "MicroMessengerX/1.0",

  # iTunes
  "iTunes/10.6.1 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/534.53.11",
  "iTunes/12.0.1 (Macintosh; OS X 10.10) AppleWebKit/0600.1.25",
  "iTunes/11.1.5 (Windows; Microsoft Windows 7 x64 Business Edition Service Pack 1 (Build 7601)) AppleWebKit/537.60.15",
  "iTunes/12.0.1 (Windows; Microsoft Windows 8 x64 Home Premium Edition (Build 9200)) AppleWebKit/7600.1017.0.24",
  "iTunes/12.1 (Windows; Microsoft Windows 8.1 x64 (Build 9600)) AppleWebKit/7600.1017.0.24",
  "iTunes/10.0 (Windows; Microsoft Windows Vista Home Premium Edition Service Pack 2 (Build 6002)) AppleWebKit/533.16",
  "iTunes/9.2 (Windows; Microsoft Windows XP Professional Service Pack 3 (Build 2600)) AppleWebKit/533.16",
  "iTunes/12.5 (Windows; Microsoft Windows 10 x64 (Build 14393)) AppleWebKit/7602.1050.4.5",
  "iTunes/9.1.1",

  # AppleCoreMedia
  "AppleCoreMedia/1.0.0.12B411 (iPhone; U; CPU OS 8_1 like Mac OS X; en_gb)",
  "AppleCoreMedia/1.0.0.9A405 (iPad; U; CPU OS 5_0_1 like Mac OS X; en_us)",
  "AppleCoreMedia/1.0.0.12F69 (Apple TV; U; CPU OS 8_3 like Mac OS X; en_us)",
  "AppleCoreMedia/1.0.0.14D27 (Macintosh; U; Intel Mac OS X 10_12_3; en_us)",
  "AppleCoreMedia/1.0.0.9A405",

  # PlayStation
  "Mozilla/5.0 (PLAYSTATION 3 4.75) AppleWebKit/531.22.8 (KHTML, like Gecko)",
  "Mozilla/5.0 (PLAYSTATION 3; 1.00)",
  "Mozilla/5.0 (PlayStation Vita 3.52) AppleWebKit/537.73 (KHTML, like Gecko) Silk/3.2",
  "Mozilla/5.0 (PlayStation Vita 3.60) AppleWebKit/537.73 (KHTML, like Gecko)",
  "Mozilla/5.0 (PlayStation 4 2.57) AppleWebKit/537.73 (KHTML, like Gecko)",

  # Podcast Addict
  "Podcast Addict - Dalvik/1.6.0 (Linux; U; Android 4.4.2; LG-D631 Build/KOT49I.D63110b)",
  "Podcast Addict - Dalvik/2.1.0 (Linux; U; Android 5.1; XT1093 Build/LPE23.32-21.3)",
  "Podcast Addict - Mozilla/5.0 (Linux; U; Android 4.2.2; en-us; ALCATEL ONE TOUCH Fierce Build/JDQ39) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.2 Mobile Safari/534.30",
  "Podcast Addict - Dalvik/2.1.0 (Linux; U; Android M Build/MPZ79M)",
  "Podcast Addict - Dalvik/2.1.0",
  "Podcast Addict - Foo/1.0 (a; b)",
  "Podcast Addict -",

  # Windows Media Player and libavformat
  "NSPlayer/4.1.0.3857",
  "NSPlayer/3.2.0.3564",
  "NSPlayer/4.1.0.3936",
  "NSPlayer/7.1.0.3055",
  "NSPlayer/8.0.0.4477",
  "Windows-Media-Player/9.00.00.2980",
  "NSPlayer/9.0.0.2980 WMFSDK/9.0",
  "NSPlayer/9.0.0.3268 WMFSDK/9.0",
  "NSPlayer/10.0.0.3802 WMFSDK/10.0",
  "NSPlayer/11.0.5721.5262 WMFSDK/11.0",
  "NSPlayer/11.00.6002.18392 WMFSDK/11.00.6002.18392",
  "NSPlayer/12.00.7601.17514 WMFSDK/12.00.7601.17514",
  "NSPlayer/12.00.9200.16384 WMFSDK/12.00.9200.16384",
  "NSPlayer/12.00.9600.17031 WMFSDK/12.00.9600.17031",
  "Windows-Media-Player/12.0.9841.0",
  "NSPlayer/12.00.9651.0000 WMFSDK/12.00.9651.0000",
  "NSPlayer/7.10.0.3059",
  "NSPlayer/7.0.0.1956",
  "WMFSDK/abc",
  "Windows-Media-Player",
  "NSPlayer/4.1.0.3856",
  "Lavf/54.29.104",
  "Lavf53.32.100",

  # Bots
  "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
  "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.6099.216 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
  "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
  "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm) Chrome/116.0.1938.76 Safari/537.36",
  "Googlebot-Image/1.0",
  "Twitterbot/1.0",
  "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",
  "Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)",
  "Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)",
  "DuckDuckBot/1.1; (+http://duckduckgo.com/duckduckbot.html)",
  "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_10_1) AppleWebKit/600.2.5 (KHTML, like Gecko) Version/8.0.2 Safari/600.2.5 (Applebot/0.1; +http://www.apple.com/go/applebot)",
  "Discordbot/2.0; +https://discordapp.com",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36 facebookexternalhit/1.1 Twitterbot/1.0",

  # Command-line and library clients
  "curl/8.4.0",
  "Wget/1.21.4",
  "python-requests/2.31.0",
  "Go-http-client/1.1",
  "Ruby",
  "PostmanRuntime/7.36.0",
  "okhttp/4.12.0",
  "Java/17.0.2",
  "node-fetch/1.0 (+https://github.com/bitinn/node-fetch)",
  "axios/1.6.2",
  "Dalvik/2.1.0 (Linux; U; Android 12; SM-G991B Build/SP1A.210812.016)",

  # Garbage
  "/",
  "/foo",
  "foo",
  "12345",
  "null",
  "undefined",
  "a,b,c",
  "X/1.0,gzip(gfe)",
  "X/1.0,gzip(gfe) Y/2",
  "\"\"\"",
  "\"/x",
  "''Chrome/1",
  "'Mozilla/5.0 (Windows NT 10.0)'",
  "Mozilla/5.0 (unterminated",
  "Mozilla/5.0 (a; b; ) x",
  "Mozilla/5.0 (;; ;)",
  "Mozilla/5.0 (; a)",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Ünïcødé/1.0 Chrome/130.0",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36 ",
  " ",
  "  Chrome/100.0  ",
  "Mozilla/5.0 (X11; CrOS) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0",
  "Mozilla/5.0 (X11) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0",

  # Browsers, in-app browsers, bots and HTTP clients as they identified themselves in 2025–26, and
  # more blank, malformed and non-ASCII strings. The Rust tests also check the parser's fast paths
  # against the straightforward code they replaced over every agent here. Request headers are
  # ASCII, but push subscriptions store whatever a client sent, so non-ASCII strings get parsed too.

  # Chrome, desktop and mobile
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/109.0.0.0 Safari/537.36",
  "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/140.0.7339.101 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPad; CPU OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/140.0.7339.101 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/140.0.0.0 Safari/537.36",

  # Safari
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1.2 Safari/605.1.15",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_7_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7.2 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 16_7_10 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPad; CPU OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",

  # Firefox
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:143.0) Gecko/20100101 Firefox/143.0",
  "Mozilla/5.0 (X11; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0",
  "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:143.0) Gecko/20100101 Firefox/143.0",
  "Mozilla/5.0 (Windows NT 6.1; Win64; x64; rv:115.0) Gecko/20100101 Firefox/115.0",
  "Mozilla/5.0 (Android 15; Mobile; rv:143.0) Gecko/143.0 Firefox/143.0",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/143.0 Mobile/15E148 Safari/605.1.15",

  # Edge
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
  "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36 EdgA/140.0.0.0",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 EdgiOS/140.3485.94 Mobile/15E148 Safari/605.1.15",

  # Opera
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 OPR/123.0.0.0",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 OPR/122.0.0.0 (Edition Yx GX)",
  "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Mobile Safari/537.36 OPR/91.0.0.0",
  "Opera/9.80 (Android; Opera Mini/8.0.1807/36.1609; U; en) Presto/2.12.423 Version/12.16",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1 OPT/5.0.3",

  # Samsung Internet
  "Mozilla/5.0 (Linux; Android 14; SAMSUNG SM-S928B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/28.0 Chrome/130.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (Linux; Android 14; SAMSUNG SM-X910) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/28.0 Chrome/130.0.0.0 Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/28.0 Chrome/130.0.0.0 Safari/537.36",

  # Other browsers
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Vivaldi/7.6.3797.52",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 YaBrowser/25.8.0.0 Safari/537.36",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Whale/4.33.325.17 Safari/537.36",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Mobile/15E148 DuckDuckGo/7 Safari/605.1.15",
  "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/140.0.0.0 Mobile DuckDuckGo/5 Safari/537.36",
  "Mozilla/5.0 (Linux; Android 12; HarmonyOS; NOH-AN00; HMSCore 6.13.0.302) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.5735.196 HuaweiBrowser/15.0.4.312 Mobile Safari/537.36",
  "Mozilla/5.0 (Linux; U; Android 13; en-us; 2201117TG Build/TKQ1.221114.001) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/112.0.5615.136 Mobile Safari/537.36 XiaoMi/MiuiBrowser/14.5.1-gn",
  "Mozilla/5.0 (Linux; U; Android 10; en-US; RMX2185 Build/QP1A.190711.020) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/100.0.4896.58 UCBrowser/13.4.0.1306 Mobile Safari/537.36",

  # In-app browsers and web views
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/520.0.0.38.101;FBBV/771583218;FBDV/iPhone15,2;FBMD/iPhone;FBSN/iOS;FBSV/18.6;FBSS/3;FBID/phone;FBLC/en_US;FBOP/5;FBRV/0]",
  "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP2A.240805.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/140.0.0.0 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/520.0.0.51.109;]",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 395.0.0.25.83 (iPhone15,2; iOS 18_6; en_US; en; scale=3.00; 1179x2556; 790392312; IABMV/1)",
  "Mozilla/5.0 (Linux; Android 14; SM-S918B Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/140.0.0.0 Mobile Safari/537.36 Instagram 395.0.0.46.81 Android (34/14; 480dpi; 1080x2340; samsung; SM-S918B; dm3q; qcom; en_US; 783929831)",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [LinkedInApp]/9.31.1700",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 musical_ly_41.2.0 JsSdk/2.0 NetType/WIFI Channel/App Store ByteLocale/en Region/US RevealType/Dialog isDarkMode/1 WKWebView/1 BytedanceWebview/d8a21c6",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Snapchat/13.55.0.35 (like Safari/8621.1.15.10.7, panda)",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) GSA/384.0.786998425 Mobile/15E148 Safari/604.1",
  "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP2A.240805.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/140.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36 TwitterAndroid",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Slack/4.46.99 Chrome/138.0.7204.251 Electron/37.3.1 Safari/537.36 Sonic Slack_SSB/4.46.99",
  "Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Hotwire Native iOS; Turbo Native iOS; bridge-components: [form menu overflow]",
  "Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP2A.240805.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/140.0.0.0 Mobile Safari/537.36 Hotwire Native Android; Turbo Native Android; bridge-components: [form menu overflow];",

  # Bots and link previews
  "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; Googlebot/2.1; +http://www.google.com/bot.html) Chrome/140.0.7339.127 Safari/537.36",
  "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.7339.127 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
  "Mozilla/5.0 (compatible; Google-InspectionTool/1.0)",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.1.1 Safari/605.1.15 (Applebot/0.1; +http://www.apple.com/go/applebot)",
  "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)",
  "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)",
  "Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)",
  "Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)",
  "Mozilla/5.0+(compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)",
  "Slack-ImgProxy (+https://api.slack.com/robots)",
  "LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)",
  "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)",
  "TelegramBot (like TwitterBot)",
  "WhatsApp/2.23.20.0",
  "Mozilla/5.0 (Linux; Android 11; moto g power (2022)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36 Chrome-Lighthouse",

  # HTTP clients
  "curl/8.14.1",
  "Wget/1.25.0",
  "python-requests/2.32.4",
  "python-httpx/0.28.1",
  "Go-http-client/2.0",
  "Faraday v2.13.1",
  "node",
  "PostmanRuntime/7.45.0",
  "Dalvik/2.1.0 (Linux; U; Android 14; Pixel 8 Build/AP2A.240805.005)",

  # Blank and malformed
  " ",
  "\u{b}",
  "\u{c}\r\n",
  "\0",
  "-",
  "()",
  "(",
  ")",
  "////",
  "a/b/c",
  ",gzip(gfe)",
  "X/1.0,gzip(gfe",
  "X/1.0 ,gzip(gfe)",
  "X/1.0,(comment)",
  "X/1.0,gzip(gfe) (comment)",
  "Mozilla/5.0,(compatible; Googlebot/2.1)",
  "Mozilla/5.0 (",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64",
  "Mozilla/5.0 ((nested) comment) Chrome/140.0",
  "Mozilla/5.0  (double  spaces)  Chrome/140.0",
  "Mozilla/5.0\t(tab)\tChrome/140.0",
  "Mozilla/5.0\u{b}(vertical tab)\u{b}Chrome/140.0",
  "Mozilla/5.0 (X11;) Firefox/143.0",
  "Mozilla/5.0 (;;;) Safari/605.1.15",
  "'\"Chrome/140",
  "\"\"",
  "''",
  "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36   ",
  "   Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
  "Chrome/140.0.0.0.0.0.0.1",
  "Chrome/140a",
  "Chrome/-1",
  "Safari/17.2 Version/17.2",

  # Non-ASCII (stored push-subscription agents)
  "Mozilla/5.0 (Linux; Android 14; Ärger-Phone) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
  "Mozilla/5.0 (X11; Linux x86_64) Браузер/1.0 (Линукс; x86_64)",
  "浏览器/1.0 (中文; 测试) Chrome/140.0",
  "🦊/1.0 (🔥; 🌍) Firefox/143.0",
  "é",
  "\u{3000}Chrome/140.0",

  # U+212A KELVIN SIGN downcases to an ASCII "k", and U+0130 to "i" and a combining dot: case-
  # insensitive comparisons that only look at ASCII would get these wrong.
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWeb\u{212A}it/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15",
  "Something/1.0 (AppleWeb\u{212A}it/533.1; foo)",
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_11_1) AppleWebKit/601.2.4 (KHTML, like Gecko) Version/9.0.1 Safari/601.2.4 faceboo\u{212A}externalhit/1.1 Facebot Twitterbot/1.0",
  "Mozilla/5.0 (X11; rv:143.0) Gecko/20100101 FIREFOX/143.0",
  "Mozilla/5.0 (X11; rv:143.0) Gecko/20100101 F\u{130}REFOX/143.0",
  "\u{130}ron/1.0 Chrome/140.0",
]

def version_to_s(version)
  NilClass === version ? nil : version.to_s
end

user_agents = corpus.map do |string|
  agent = UserAgent.parse(string)
  platform = ApplicationPlatform.new(string)

  {
    "ua" => string,
    "browser" => rb { agent.browser },
    "version" => rb { version_to_s(agent.version) },
    "platform" => rb { agent.platform },
    "os" => rb { agent.os },
    "bot" => rb { agent.bot? },
    "mobile" => rb { agent.mobile? },
    "application_platform" => {
      "ios" => rb { platform.ios? },
      "android" => rb { platform.android? },
      "mac" => rb { platform.mac? },
      "chrome" => rb { platform.chrome? },
      "firefox" => rb { platform.firefox? },
      "safari" => rb { platform.safari? },
      "edge" => rb { platform.edge? },
      "apple_messages" => rb { platform.apple_messages? },
      "mobile" => rb { platform.mobile? },
      "desktop" => rb { platform.desktop? },
      "windows" => rb { platform.windows? },
      "operating_system" => rb { platform.operating_system },
      "browser" => rb { platform.browser }
    },
    "blocked" => rb {
      ActionController::AllowBrowser::BrowserBlocker.new(Struct.new(:user_agent).new(string), versions: AllowBrowser::VERSIONS).blocked?
    }
  }
end

version_strings = [
  "", " ", "0", "1", "1.0", "1.0.0", "01.0", "10", "010", "17", "17.1", "17.2", "17.2.0", "17.2.1", "17.10",
  "120", "119.0.6045.199", "120.0.0.0", "124.0.6367.88", "1.2.3.4.5.6.7", "1.2.3.4.5.6.8", "1.2.3.4.5.7",
  "1.0b", "1.0beta", "1.0-beta", "1.0.beta", "1.2beta.3", "1.a", "abc", "abd", "ABC", "v1.0", ".1", "1.",
  "1..2", "17.2abc", "4.1.0.3856", "12.00.9600.17031", "99999999999999999999999", "99999999999999999999998",
  "0x18002a2c", "8.0.42(0x18002a2c)", "1_2", "1 2"
]

versions = version_strings.map do |string|
  version = UserAgent::Version.new(string)
  { "string" => string, "nil" => version.nil?, "to_a" => version.to_a.map { |part| part.is_a?(Integer) ? "i:#{part}" : "s:#{part}" } }
end

comparisons = version_strings.product([ "", "1.0", "17.2", "120", "abc", "1.2.3.4.5.6.8", "1.0b", "99999999999999999999999" ]).map do |a, b|
  va, vb = UserAgent::Version.new(a), UserAgent::Version.new(b)
  { "a" => a, "b" => b, "cmp" => (va <=> vb), "lt" => va < vb, "eq" => va == vb }
end

puts JSON.pretty_generate("user_agents" => user_agents, "versions" => versions, "comparisons" => comparisons)
