# Task status

A task from [`../tasks.yaml`](../tasks.yaml) is **done** when `plans/status/<ID>.md` exists on
`main`. Each task writes only its own file, so parallel agents never conflict here.

Write the file in the same PR that completes the task:

```markdown
# <ID> <title>

- PR: <link>
- Commit: <sha>
- Agent/session: <who did it>

## Acceptance evidence

- [x] <criterion copied from tasks.yaml>: <command run, test name, or report path proving it>
- [x] ...

## Notes for dependents

<interfaces introduced, decisions made, anything the next tasks need to know>

## Known gaps

<anything deferred, with the task id that will pick it up>
```

`plans/bin/plan ready` reads this directory to work out which tasks can be claimed next.
