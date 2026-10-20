profile: mixed-read-write-v1
seed: 6b967f4b6d060ccb57229c2a0add5b6c1680bf0bd873d672108094434095b790
verification: ec02debb3e44f40b8778a6a42db3c4515b2f9eaf
rounds: 3 duration: 8s cpus: 7-10 client: 3-6
raw_samples: 168 validated_responses: 24561185 errors: 0 invalid: 0

req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 781 | 13,239 | 38,089 | 24,160 | 16,984 | 469 | 52,414 |
| Messages page | 932 | 54,081 | 36,289 | 25,293 | 32,706 | 709 | 54,162 |
| Sidebar | 1,958 | 74,145 | 48,083 | 34,271 | 48,588 | 1,983 | 62,818 |
| Search | 1,624 | 68,039 | 46,143 | 32,773 | 42,316 | 1,530 | 62,406 |

p99 ms

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 278.53 | 9.17 | 0.81 | 3.86 | 7.95 | 167.17 | 0.69 |
| Messages page | 210.05 | 2.27 | 0.86 | 3.42 | 2.96 | 111.55 | 0.69 |
| Sidebar | 35.84 | 0.41 | 0.70 | 2.01 | 1.09 | 19.74 | 0.55 |
| Search | 63.23 | 0.62 | 0.73 | 2.26 | 0.94 | 44.19 | 0.54 |

× Rails req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 1.00 | 16.95 | 48.78 | 30.94 | 21.75 | 0.60 | 67.12 |
| Messages page | 1.00 | 58.05 | 38.95 | 27.15 | 35.11 | 0.76 | 58.14 |
| Sidebar | 1.00 | 37.87 | 24.56 | 17.50 | 24.82 | 1.01 | 32.08 |
| Search | 1.00 | 41.90 | 28.42 | 20.18 | 26.06 | 0.94 | 38.43 |

  rails edbc779f4dfc
  csharp 513ab6d6c9e9
  rust 6dae2fd3c1a2
  go c4ab53d00b0a
  express bb70a7d47816
  laravel 4093bf2de61d
  c 873a51f523de
  rails room_show runs=[733.6, 780.9, 800.1] spread=8.52%
  csharp room_show runs=[16808.9, 12411.2, 13239.1] spread=33.22%
  rust room_show runs=[37659.9, 38089.1, 38092.6] spread=1.14%
  go room_show runs=[24160.1, 23824.1, 24283.9] spread=1.90%
  express room_show runs=[17103.3, 16983.9, 16732.7] spread=2.18%
  laravel room_show runs=[470.9, 321.5, 469.4] spread=31.83%
  c room_show runs=[52886.3, 52413.6, 51267.1] spread=3.09%
  rails messages_page runs=[869.6, 931.6, 961.0] spread=9.81%
  csharp messages_page runs=[56111.5, 54081.2, 51835.7] spread=7.91%
  rust messages_page runs=[36122.7, 36288.7, 36694.0] spread=1.57%
  go messages_page runs=[25021.7, 25293.1, 25392.6] spread=1.47%
  express messages_page runs=[36420.3, 27904.8, 32706.1] spread=26.04%
  laravel messages_page runs=[710.4, 659.4, 708.8] spread=7.20%
  c messages_page runs=[55042.3, 53816.2, 54161.7] spread=2.26%
  rails sidebar runs=[1987.7, 1935.3, 1957.9] spread=2.68%
  csharp sidebar runs=[76762.7, 74144.9, 73706.8] spread=4.12%
  rust sidebar runs=[47461.7, 48083.4, 48139.3] spread=1.41%
  go sidebar runs=[34240.2, 34375.7, 34271.0] spread=0.40%
  express sidebar runs=[50528.1, 47889.8, 48587.8] spread=5.43%
  laravel sidebar runs=[1975.1, 1993.9, 1983.4] spread=0.95%
  c sidebar runs=[62851.0, 62156.1, 62817.5] spread=1.11%
  rails search runs=[1665.8, 1573.3, 1623.7] spread=5.70%
  csharp search runs=[69639.6, 68039.4, 67255.1] spread=3.50%
  rust search runs=[45606.6, 46143.2, 46480.6] spread=1.89%
  go search runs=[32719.7, 32802.5, 32772.8] spread=0.25%
  express search runs=[42889.5, 42316.2, 42111.8] spread=1.84%
  laravel search runs=[1527.1, 1529.8, 1542.3] spread=0.99%
  c search runs=[62406.2, 60816.3, 63265.6] spread=3.92%
