profile: read-and-post-v1
seed: 6b967f4b6d060ccb57229c2a0add5b6c1680bf0bd873d672108094434095b790
verification: ec02debb3e44f40b8778a6a42db3c4515b2f9eaf
rounds: 3 duration: 8s cpus: 7-10 client: 3-6
raw_samples: 336 validated_responses: 73328538 errors: 0 invalid: 0

req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 2,388 | 37,115 | 42,167 | 28,555 | 20,880 | 1,999 | 58,248 |
| Messages page | 2,183 | 71,154 | 40,027 | 29,344 | 40,521 | 2,062 | 60,615 |
| Sidebar | 2,407 | 78,359 | 48,775 | 32,711 | 49,147 | 2,283 | 64,849 |
| Search | 2,491 | 76,385 | 48,822 | 33,112 | 46,937 | 2,171 | 66,091 |
| Post a message | 265 | 2,598 | 3,198 | 3,264 | 1,228 | 463 | 1,729 |

p99 ms

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 17.45 | 2.39 | 0.71 | 2.39 | 3.52 | 16.21 | 0.59 |
| Messages page | 24.57 | 0.39 | 0.75 | 2.33 | 1.01 | 16.09 | 0.58 |
| Sidebar | 19.71 | 0.31 | 0.65 | 2.09 | 0.89 | 14.94 | 0.51 |
| Search | 18.41 | 0.29 | 0.66 | 2.11 | 0.71 | 15.46 | 0.47 |
| Post a message | 158.85 | 12.64 | 14.61 | 18.19 | 65.47 | 83.26 | 17.25 |

× Rails req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 1.00 | 15.54 | 17.66 | 11.96 | 8.75 | 0.84 | 24.40 |
| Messages page | 1.00 | 32.60 | 18.34 | 13.44 | 18.56 | 0.94 | 27.77 |
| Sidebar | 1.00 | 32.55 | 20.26 | 13.59 | 20.42 | 0.95 | 26.94 |
| Search | 1.00 | 30.67 | 19.60 | 13.29 | 18.84 | 0.87 | 26.53 |
| Post a message | 1.00 | 9.79 | 12.05 | 12.30 | 4.63 | 1.74 | 6.52 |

  rails edbc779f4dfc
  csharp 513ab6d6c9e9
  rust 6dae2fd3c1a2
  go c4ab53d00b0a
  express bb70a7d47816
  laravel 4093bf2de61d
  c 873a51f523de
  rails room_show runs=[2348.5, 2436.8, 2387.6] spread=3.70%
  csharp room_show runs=[39049.4, 37114.6, 34410.3] spread=12.50%
  rust room_show runs=[42167.0, 42062.7, 42728.0] spread=1.58%
  go room_show runs=[28692.7, 28554.6, 28478.7] spread=0.75%
  express room_show runs=[20879.9, 21269.5, 20507.8] spread=3.65%
  laravel room_show runs=[1982.5, 2001.4, 1999.4] spread=0.95%
  c room_show runs=[58248.1, 56918.8, 58418.9] spread=2.58%
  rails messages_page runs=[2383.4, 2173.7, 2182.9] spread=9.61%
  csharp messages_page runs=[71513.3, 70486.5, 71153.8] spread=1.44%
  rust messages_page runs=[39967.4, 40026.7, 40675.0] spread=1.77%
  go messages_page runs=[29404.8, 29229.5, 29344.2] spread=0.60%
  express messages_page runs=[40520.8, 40773.5, 39884.3] spread=2.19%
  laravel messages_page runs=[2054.3, 2072.5, 2061.9] spread=0.88%
  c messages_page runs=[61890.2, 59785.3, 60614.6] spread=3.47%
  rails sidebar runs=[2558.8, 2398.3, 2407.1] spread=6.67%
  csharp sidebar runs=[78359.0, 77573.9, 78481.0] spread=1.16%
  rust sidebar runs=[48083.5, 48774.7, 48882.6] spread=1.64%
  go sidebar runs=[32889.7, 32617.9, 32711.1] spread=0.83%
  express sidebar runs=[49147.0, 53153.0, 40174.9] spread=26.41%
  laravel sidebar runs=[2272.3, 2285.1, 2283.4] spread=0.56%
  c sidebar runs=[64849.4, 65013.6, 63746.6] spread=1.95%
  rails search runs=[2595.2, 2472.6, 2490.8] spread=4.92%
  csharp search runs=[76565.9, 75742.8, 76385.4] spread=1.08%
  rust search runs=[48822.5, 48581.6, 49276.4] spread=1.42%
  go search runs=[33111.5, 32951.5, 33143.5] spread=0.58%
  express search runs=[44274.3, 47454.3, 46936.8] spread=6.78%
  laravel search runs=[2166.2, 2170.6, 2175.0] spread=0.41%
  c search runs=[66907.9, 66090.6, 63239.3] spread=5.55%
  rails post_message runs=[265.4, 266.1, 264.7] spread=0.53%
  csharp post_message runs=[2595.5, 2646.8, 2598.2] spread=1.97%
  rust post_message runs=[3198.0, 3264.6, 3194.1] spread=2.20%
  go post_message runs=[3264.2, 3285.3, 3180.2] spread=3.22%
  express post_message runs=[1228.4, 1222.0, 1238.9] spread=1.38%
  laravel post_message runs=[463.6, 458.7, 462.6] spread=1.06%
  c post_message runs=[1729.4, 1737.0, 1704.3] spread=1.89%
