---
name: commit-messages
description: Use when writing a git commit message. Produces a Conventional Commits subject plus a short bulleted body — concise, simple sentences, one intent per commit. No novels, no walls of prose.
---

# Commit messages

A commit message explains **what changed and why** in the fewest words that are still meaningful. One commit = one intent. If you can't describe it without "and", it's probably two commits.

## Format

Follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
<type>(<optional scope>): <subject>

- <bullet describing one change>
- <bullet describing another change>

<optional footer>
```

**Subject line** — `type(scope): subject`
- Lowercase, imperative mood ("add", "fix", "remove" — not "added"/"adds").
- No trailing period. Aim for ≤ 50 chars, hard cap ~72.
- `scope` is optional and is the area touched, e.g. `feat(auth): ...`.

**Types:**

| Type | Use for |
|------|---------|
| `feat` | a new feature (user-visible capability) |
| `fix` | a bug fix |
| `docs` | documentation only |
| `refactor` | code change that neither fixes a bug nor adds a feature |
| `perf` | a performance improvement |
| `test` | adding or fixing tests |
| `build` | build system, dependencies |
| `ci` | CI configuration |
| `chore` | maintenance, no src/test change |
| `style` | formatting/whitespace, no behavior change |

## Body — bullets, not paragraphs

- Use `-` bullets. One change or reason per bullet.
- Simple sentences. One subject per sentence. Max 2–3 short sentences per bullet — usually one is enough.
- Say **why** when it isn't obvious from the diff. Skip restating the obvious.
- Omit the body entirely for trivial commits — the subject is enough.

## Breaking changes

- Add `!` after the type/scope: `feat(api)!: drop v1 token format`.
- And/or a footer: `BREAKING CHANGE: <what broke and the migration>`.

## One intent per commit

- Default to one logical thing per commit. Don't mix a feature with an unrelated refactor.
- If the message needs multiple `feat:`/`fix:` ideas, split into multiple commits.
- A formatting sweep goes in its own `style:` commit, separate from behavior changes.
- **Exception:** if the user explicitly asks to combine two changes into one commit, do it — keep the bulleted format and give each intent its own bullet (or small group) so the result is still easy to scan. Pick the subject type that covers the primary change.

## Focus on what and why, not the test play-by-play

- Describe **what** changed and **why** it matters — the behavior, the bug, the reason.
- Don't narrate the testing regime in gory detail. "Add tests" or `test:` is enough; the diff shows the rest.
- Mention tests only when it's the *point* of the commit (a `test:` commit) or when a test reveals something worth knowing (e.g. "covers the expired-token edge case that regressed").

## Examples

Good:

```
fix(auth): reject expired refresh tokens

- Return 401 when a refresh token is past its TTL.
- The old code compared against issue time, so stale tokens still refreshed.
```

```
feat(cache): add per-entry expiry
```

```
refactor(parser): extract token reader into its own type

- Splits the 200-line scan loop so each token kind is testable.
```

Avoid:
- A single commit titled `update stuff` mixing a feature, a fix, and reformatting.
- A 6-paragraph body re-explaining the whole diff line by line.
- Past tense / sentence-case subjects (`Added the thing.`).
- A body bullet that runs four sentences and changes subject mid-bullet.

## Checklist

- [ ] Subject is `type(scope): imperative subject`, lowercase, no period
- [ ] One intent per commit — unless the user asked to combine, then one bullet per intent
- [ ] Body (if any) is `-` bullets, simple sentences, ≤ 2–3 per bullet
- [ ] Body explains *what* and *why*, not the test play-by-play
- [ ] Breaking changes marked with `!` and/or `BREAKING CHANGE:` footer
