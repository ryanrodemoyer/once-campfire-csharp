# once-campfire-csharp

Campfire in C#: a work-in-progress port of [ONCE Campfire](https://github.com/basecamp/once-campfire)
that aims to behave the same as the Rails app, so it can be benchmarked fairly next to the
[other Campfire implementations](https://github.com/basecamp/once-campfire#other-implementations).

Nothing runs yet. The plan, task graph and progress are in [`plans/`](plans/csharp-port.md), and
the work is tracked as [GitHub issues](../../issues). No benchmark numbers will be published until
the port matches the reference.

## Known differences

Where the port deliberately behaves differently from the Rails reference, and why.

- **The writer checkpoints a large WAL.** Rails' `lib/rails_ext/sqlite_wal_checkpoint.rb`
  checkpoints only from a background contender (and once at exit). Under a steady stream of
  commits, that contender's PASSIVE checkpoint never catches up between two of them, so the WAL
  never restarts and keeps growing. The port's writer runs its own PASSIVE checkpoint after a
  commit once the WAL passes 4 MiB, SQLite's default `wal_autocheckpoint` of 1,000 pages
  ([`WriterCheckpointFallback`](src/Campfire.Data/Checkpoint/WriterCheckpointFallback.cs)).
  Gate: `WAL_size_stays_bounded_under_a_sustained_posting_run_and_stop_leaves_the_database_consistent`
  in [`SqliteWalCheckpointTests`](tests/Campfire.Data.Tests/Checkpoint/SqliteWalCheckpointTests.cs),
  which fails without the fallback: the WAL grows past its 8 MiB bound (17 to 31 MB in 1,000
  posts on a development machine; see [`plans/status/U08.md`](plans/status/U08.md)).

## License

MIT; see [MIT-LICENSE](MIT-LICENSE).
