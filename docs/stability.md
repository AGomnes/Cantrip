# Stability

Cantrip is in preview (0.x). It works, it is tested, and it has been used to build and play a small roguelite, but nobody has shipped a game with it yet. Expect it to change as that happens.

## What may change between previews

- **The C# API.** Names, signatures and types may change. The [changelog](../CHANGELOG.md) lists every change that could break a caller.
- **The language.** Keywords, verbs and their defaults may change, though the aim is to keep content that works today working, and to make any change that breaks it an error at load time rather than a silent change in behaviour. That is why `event` and `encounter`, which have no meaning yet, are an error (CT0113) instead of being quietly ignored.
- **The save format.** A save made with one preview may not load in the next. `GameSnapshot.FormatVersion` changes when it cannot, and loading an older one is refused with a clear error rather than misread.
- **Diagnostic codes.** Codes may be added; an existing code keeps its meaning.

## What will not change without a major version, once 1.0 is out

The public API, the language, and the ability to load a save made with any earlier 1.x release.

## Determinism

The same content, seed and inputs produce the same game on every machine: fixed-point maths, a seeded generator and explicit orderings, with no floating point or hash-order dependence in the rules. CI plays the same simulated games on Linux and Windows, and they have matched to the last figure. A difference between machines is a bug, and the most important kind to report.

## Real time

The tick clock and abilities work and are tested, but no real-time game has been built with them yet, and spatial queries (`within`) come from the host. Treat real time as experimental.

## Reporting a problem

Open an issue on [GitHub](https://github.com/AGomnes/Cantrip/issues) with the smallest `.cantrip` file that shows it, what you expected, and what happened. `cantrip --version` names the exact build. For a problem with the rules, a failing `test` block is the best report there is: it is both the description and the proof.
