profile: read-and-post-v1
seed: 6b967f4b6d060ccb57229c2a0add5b6c1680bf0bd873d672108094434095b790
verification: ec02debb3e44f40b8778a6a42db3c4515b2f9eaf
rounds: 3 duration: 8s cpus: 7-10 client: 3-6
raw_samples: 336 validated_responses: 73532696 errors: 0 invalid: 0

req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 2,400 | 35,313 | 41,952 | 28,640 | 19,458 | 1,988 | 58,264 |
| Messages page | 2,239 | 71,056 | 39,722 | 28,934 | 40,308 | 2,049 | 63,098 |
| Sidebar | 2,402 | 78,093 | 48,317 | 32,624 | 52,506 | 2,264 | 64,352 |
| Search | 2,505 | 76,381 | 48,489 | 33,174 | 46,862 | 2,164 | 62,753 |
| Post a message | 262 | 2,650 | 3,217 | 3,175 | 1,221 | 459 | 1,728 |

p99 ms

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 17.28 | 2.59 | 0.71 | 2.37 | 3.42 | 16.50 | 0.59 |
| Messages page | 22.77 | 0.39 | 0.75 | 2.37 | 0.98 | 16.13 | 0.55 |
| Sidebar | 20.82 | 0.30 | 0.66 | 2.12 | 0.91 | 14.99 | 0.52 |
| Search | 18.11 | 0.30 | 0.66 | 2.10 | 0.70 | 15.65 | 0.54 |
| Post a message | 169.73 | 12.49 | 14.54 | 18.86 | 65.22 | 82.30 | 16.86 |

× Rails req/s

| Workload | Rails | C# | Rust | Go | Express | Laravel | C |
|---|---:|---:|---:|---:|---:|---:|---:|
| Room page | 1.00 | 14.71 | 17.48 | 11.93 | 8.11 | 0.83 | 24.27 |
| Messages page | 1.00 | 31.74 | 17.74 | 12.92 | 18.00 | 0.92 | 28.18 |
| Sidebar | 1.00 | 32.51 | 20.11 | 13.58 | 21.86 | 0.94 | 26.79 |
| Search | 1.00 | 30.49 | 19.36 | 13.24 | 18.71 | 0.86 | 25.05 |
| Post a message | 1.00 | 10.13 | 12.30 | 12.14 | 4.67 | 1.76 | 6.61 |

  rails edbc779f4dfc
  csharp 513ab6d6c9e9
  rust 6dae2fd3c1a2
  go c4ab53d00b0a
  express bb70a7d47816
  laravel 4093bf2de61d
  c 873a51f523de
  rails room_show runs=[2392.1, 2400.2, 2406.4] spread=0.60%
  csharp room_show runs=[33727.5, 35313.4, 36756.5] spread=8.58%
  rust room_show runs=[42140.7, 41479.3, 41951.5] spread=1.58%
  go room_show runs=[28694.4, 28639.7, 28553.6] spread=0.49%
  express room_show runs=[20538.7, 16759.8, 19457.8] spread=19.42%
  laravel room_show runs=[1993.6, 1985.6, 1987.6] spread=0.40%
  c room_show runs=[58896.8, 56869.7, 58264.3] spread=3.48%
  rails messages_page runs=[2239.0, 2163.5, 2245.0] spread=3.64%
  csharp messages_page runs=[70696.3, 71056.5, 71312.8] spread=0.87%
  rust messages_page runs=[39949.4, 39541.2, 39721.8] spread=1.03%
  go messages_page runs=[28933.8, 28885.2, 29259.8] spread=1.29%
  express messages_page runs=[40308.5, 40536.8, 39861.4] spread=1.68%
  laravel messages_page runs=[2046.0, 2059.8, 2049.1] spread=0.67%
  c messages_page runs=[61255.5, 63097.7, 64328.5] spread=4.87%
  rails sidebar runs=[2435.5, 2402.3, 2391.5] spread=1.83%
  csharp sidebar runs=[78027.2, 78092.8, 79200.9] spread=1.50%
  rust sidebar runs=[48317.0, 48491.1, 47818.8] spread=1.39%
  go sidebar runs=[32648.7, 32587.9, 32623.7] spread=0.19%
  express sidebar runs=[52506.5, 52608.1, 52021.6] spread=1.12%
  laravel sidebar runs=[2127.9, 2271.7, 2263.7] spread=6.35%
  c sidebar runs=[64351.9, 62576.9, 64624.3] spread=3.18%
  rails search runs=[2519.1, 2504.9, 2428.9] spread=3.60%
  csharp search runs=[76381.0, 75952.4, 76416.9] spread=0.61%
  rust search runs=[48591.3, 48138.2, 48488.9] spread=0.93%
  go search runs=[33092.2, 33249.0, 33174.3] spread=0.47%
  express search runs=[46861.8, 47067.8, 45913.2] spread=2.46%
  laravel search runs=[2163.5, 2173.8, 2149.1] spread=1.14%
  c search runs=[65324.9, 62753.2, 61611.2] spread=5.92%
  rails post_message runs=[261.5, 264.5, 260.1] spread=1.68%
  csharp post_message runs=[2662.6, 2650.3, 2625.2] spread=1.41%
  rust post_message runs=[3176.2, 3217.0, 3220.3] spread=1.37%
  go post_message runs=[3175.2, 3195.6, 3144.4] spread=1.61%
  express post_message runs=[1220.6, 1243.2, 1207.0] spread=2.97%
  laravel post_message runs=[459.0, 463.2, 452.1] spread=2.42%
  c post_message runs=[1701.7, 1728.1, 1820.5] spread=6.87%
