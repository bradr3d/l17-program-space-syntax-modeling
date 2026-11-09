# Program Space Syntax Modeling

## Summary

## Solver Viewer

To get the solver output (as of 10/24/26), run:

```
dotnet run --project tools/Psm.Viz.Cli -- samples/program.golden.json --out viz-out
```

This needs to be run from the repo root.

Options:

| Flag | Default | Meaning |
|---|---|---|
| `--out DIR` | `viz-out` | Output folder |
| `--every N` | `10` | Keep every Nth solver iteration in the replay (lower = smoother, bigger file) |
| `--seed N` | `42` | Random seed for the initial placement |

Sample program comes from `dal-init` branch, for now:

```
git show origin/dal-init:tests/Psm.Data.Tests/Fixtures/program.golden.json > samples/program.golden.json
```

NOTE: The `program.golden.json` file will need to be moved to something like:
`tests/Psm.Data.Tests/Fixtures/program.golden.json` instead, to avoid merge conflicts!