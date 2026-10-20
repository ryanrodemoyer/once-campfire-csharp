profile: mixed-read-write-v1
seed: 6b967f4b6d060ccb57229c2a0add5b6c1680bf0bd873d672108094434095b790
verification: ec02debb3e44f40b8778a6a42db3c4515b2f9eaf
rounds: 3 duration: 8s cpus: 7-10 client: 3-6
raw_samples: 168 validated_responses: 24549382 errors: 0 invalid: 0

req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 800 | 13,094 | 37,952 | 24,042 | 16,329 | 374 | 51,281 |
| Messages page | 925 | 54,587 | 36,106 | 25,619 | 35,975 | 705 | 54,555 |
| Sidebar | 2,012 | 74,270 | 47,811 | 34,186 | 49,636 | 1,991 | 62,521 |
| Search | 1,726 | 67,739 | 45,765 | 32,704 | 42,624 | 1,531 | 59,690 |

p99 ms

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 300.03 | 8.73 | 0.81 | 3.87 | 8.42 | 182.01 | 0.77 |
| Messages page | 250.75 | 1.80 | 0.87 | 3.48 | 2.99 | 108.16 | 0.68 |
| Sidebar | 34.34 | 0.41 | 0.70 | 2.02 | 0.80 | 19.81 | 0.55 |
| Search | 60.00 | 0.71 | 0.74 | 2.29 | 0.94 | 44.77 | 0.60 |

× Rails req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 1.00 | 16.36 | 47.41 | 30.03 | 20.40 | 0.47 | 64.06 |
| Messages page | 1.00 | 59.00 | 39.03 | 27.69 | 38.88 | 0.76 | 58.97 |
| Sidebar | 1.00 | 36.92 | 23.77 | 16.99 | 24.67 | 0.99 | 31.08 |
| Search | 1.00 | 39.24 | 26.51 | 18.95 | 24.69 | 0.89 | 34.58 |

  rails edbc779f4dfc
  csharp 513ab6d6c9e9
  rust 6dae2fd3c1a2
  go c4ab53d00b0a
  express bb70a7d47816
  laravel 4093bf2de61d
  c 873a51f523de
  rails room_show runs=[875.6, 759.4, 800.5] spread=14.52%
  csharp room_show runs=[14119.1, 13094.0, 13064.1] spread=8.06%
  rust room_show runs=[37952.1, 38120.9, 37799.7] spread=0.85%
  go room_show runs=[23953.8, 24511.5, 24041.7] spread=2.32%
  express room_show runs=[16919.5, 16118.3, 16328.8] spread=4.91%
  laravel room_show runs=[374.3, 338.9, 411.1] spread=19.29%
  c room_show runs=[51280.9, 52764.7, 50949.4] spread=3.54%
  rails messages_page runs=[917.4, 925.2, 992.0] spread=8.06%
  csharp messages_page runs=[55150.8, 53507.6, 54587.3] spread=3.01%
  rust messages_page runs=[35982.4, 36197.0, 36106.1] spread=0.59%
  go messages_page runs=[24909.1, 25634.4, 25618.6] spread=2.83%
  express messages_page runs=[36241.0, 35974.6, 35971.5] spread=0.75%
  laravel messages_page runs=[704.6, 698.5, 706.7] spread=1.16%
  c messages_page runs=[55462.8, 53369.4, 54554.9] spread=3.84%
  rails sidebar runs=[1983.7, 2024.8, 2011.8] spread=2.04%
  csharp sidebar runs=[74270.2, 75513.5, 73919.0] spread=2.15%
  rust sidebar runs=[47646.8, 47851.5, 47811.1] spread=0.43%
  go sidebar runs=[33806.1, 34185.6, 34536.8] spread=2.14%
  express sidebar runs=[50036.3, 49636.3, 47284.5] spread=5.54%
  laravel sidebar runs=[1980.2, 1990.9, 2016.4] spread=1.82%
  c sidebar runs=[62520.8, 62187.5, 63094.6] spread=1.45%
  rails search runs=[1743.7, 1726.2, 1668.0] spread=4.39%
  csharp search runs=[67739.2, 68140.5, 66891.8] spread=1.84%
  rust search runs=[45714.5, 46003.4, 45764.6] spread=0.63%
  go search runs=[32185.1, 32839.2, 32704.2] spread=2.00%
  express search runs=[41215.5, 42623.7, 42976.3] spread=4.13%
  laravel search runs=[1515.1, 1536.4, 1530.8] spread=1.39%
  c search runs=[59690.2, 58337.0, 60125.8] spread=3.00%
