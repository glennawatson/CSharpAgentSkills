---
name: csharp-docs
description: Use when writing or reviewing C# XML documentation comments. Produces concise docs that state what a member does - no remarks unless unusual behavior is method-wide and not obvious from the code, inline comments at the confusing statement instead, nothing about past implementations, no cref/example/see-langword spam.
---

# C# XML docs — concise and meaningful

Good docs tell a caller what the signature can't: the contract, the surprising behavior, the *why*. They are **not** a restatement of the method name in a full sentence. Write less; mean more.

## The bar for every doc comment

Before writing, ask: **"Does this tell the reader something the signature doesn't?"** If no, either the name needs fixing or the comment isn't worth writing. `public void Save()` does not need `<summary>Saves.</summary>`.

Document, in priority order: the **contract** (what's guaranteed, what's required), **non-obvious behavior** (side effects, throwing conditions, null handling, threading), and **why** when the reason isn't evident. Skip the obvious.

The readers are experts in the platform the code targets - Roslyn, the UI frameworks, Rx. Don't teach them what they already read fluently. We are writing a library, not a thesis.

## Inline comments over remarks

Where code is genuinely confusing, put a short `//` comment on the confusing statement. That is the reference form: it sits exactly where the reader is stuck, and it leaves with the code it explains. A `<remarks>` block describing the implementation from a distance is the wrong tool for the same job; a remark is only for unusual behavior that is method-wide and cannot be seen from the code itself.

```csharp
// InvokeRequired is false until the control or a parent has a handle, so this runs inline until then.
if (!control.InvokeRequired)
```

## Summaries

One sentence, present-tense, third-person. State what it *does* / *returns*, not how it is implemented.

Where an analyzer requires docs on every element - a summary, a `<param>` per parameter, `<returns>` - those required elements stay, and each is one concise line saying what the member does.

```csharp
/// <summary>Returns the cached user, fetching from the store on a miss.</summary>
public User GetUser(Guid id) { ... }
```

Conventions worth keeping (they read naturally and tools expect them):
- Properties: `Gets the …` (read-only) / `Gets or sets the …` (read-write).
- Constructors: `Initializes a new instance of the <see cref="Thing"/> class.`
- Bool params/returns: `true` to …; otherwise, `false`.

## Use the heavyweight tags sparingly — that's the whole point

- **`<remarks>`** — *removed by default*. Keep one only when the unusual behavior spans the whole method (or type) and is not obvious from reading the code itself. Unusual behavior confined to one statement or branch is an inline `//` comment on that statement, never a remark. When reviewing, delete every remark that does not meet that bar. Most members, and most types, have none.
- **`<example>`** — only when usage is genuinely non-obvious from the signature. A trivial call is not an example. Never add one just to fill a section.
- **`<see cref="..."/>`** — link the *first, most relevant* reference, not every type you mention. Crefs are clickable noise when overused; a paragraph with five of them is unreadable. Link what the reader would actually navigate to.
- **`<see langword="null|true|false|async"/>`** — fine occasionally for real keywords; don't langword-wrap every "null" and "true" in the prose. Plain backticks-in-prose intent is enough most of the time.
- **`<paramref>` / `<typeparamref>`** — use when you genuinely refer to a parameter by name in prose.

## Params, returns, exceptions

Document a `<param>` when it adds info (units, valid range, null behavior, ownership). Don't write `<param name="id">The id.</param>` — that's noise; omit it or say something real (`The user id; must not be <see cref="Guid.Empty"/>.`). Same for `<returns>`: describe meaning and edge cases (what an empty/null result means), not "the result."

Document `<exception>` for exceptions a caller should reasonably catch or guard against — `ArgumentNullException`, domain-specific failures. Don't enumerate every theoretically-possible throw.

## What to avoid

- **Novels.** A `<summary>` that runs three sentences is usually two too many. Cut the rest; if one point is truly confusing, it becomes an inline comment where the confusion is.
- **History.** Comments describe the code as it is now — never mention how it used to work, what it replaced, which approach was tried first, or why an older design was dropped. Banned words: "now", "previously", "used to", "already", "no longer", "still", "as before", "originally". A reader six months out has no interest in the journey, only the current state.
- **Comments on something absent.** A comment explaining why a property is missing, a flag is off, or an approach was rejected describes the change, not the code — absent code cannot confuse a reader, so it never earns a comment.
- **Implementation narration.** A summary that walks through the steps the body takes. The body already says that.
- **Restating the obvious.** `<param name="count">The count.</param>`, `<summary>Gets or sets the Name.</summary>` on a property literally called `Name` with an obvious meaning.
- **cref / see-langword spam.** Decoration that hurts readability.
- **Examples for trivial APIs.** If `Add(a, b)` needs an example, the name is the problem.
- **Doc'ing private trivia.** Public/protected API earns docs; private helpers earn a good name and an occasional `//` only where logic is subtle. Where an analyzer forces docs on private members, give them one line and nothing more.

## Quick test

A reviewer should be able to read the comment in two seconds and learn something. If they'd skim past it because it just echoes the code, delete it. The best XML doc comment is often shorter than the one you first wrote.
