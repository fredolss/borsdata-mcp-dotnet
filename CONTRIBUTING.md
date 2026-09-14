# Contributing

Bug reports, feature requests, and pull requests are all welcome. See
[README.md](README.md) for what this project does and how to run it.

## Getting set up

```bash
cp .env.example .env   # fill in Borsdata__ApiKey
dotnet build
dotnet test
./run-dev.sh            # or run-dev.cmd on Windows
```

## Making changes

- Branch off `main`.
- Keep pull requests focused — one change per PR.
- There's no `.editorconfig` in this repo; match the style already used in
  the file you're editing.

## Tests

Unit tests live in `tests/BorsdataMcp.Tests` and use xUnit. Test files
follow a `<ClassUnderTest>Tests.cs` naming convention (see
[AuthKeyHandlerTests.cs](tests/BorsdataMcp.Tests/AuthKeyHandlerTests.cs) for
an example). Add or update tests for any behavior change, and run
`dotnet test` before opening a PR.

## Architecture notes

[CLAUDE.md](CLAUDE.md) documents the non-obvious design decisions in this
codebase (e.g. why logging must go to stderr, why `RateLimitHandler` is
registered as a singleton). Worth a read before making structural changes.

## Submitting a pull request

Push your branch and open a PR against `main`. CI
(`.github/workflows/build.yml`) runs `dotnet build` and `dotnet test`
automatically on every PR — make sure it's green before requesting review.

## Reporting bugs or requesting features

Open a GitHub issue. There's no issue template, so for bug reports please
include steps to reproduce and what you expected to happen instead.

## License

By contributing, you agree that your contributions will be licensed under
this project's [MIT license](LICENSE).
