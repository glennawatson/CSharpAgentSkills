# Repository guide

This repository holds agent skills for C# and .NET work. It contains no buildable solution.

## Layout

- `skills/<name>/SKILL.md`: one skill per folder. The folder name must match the `name` in the frontmatter.
- Companion files (helpers, examples, targets) live beside the `SKILL.md` that references them.

## Writing skills

- Frontmatter has `name` (lowercase, hyphenated) and `description`. The description says when to use the skill; agents choose skills from it.
- Keep skills agent-agnostic. Do not name agent-specific tools, paths or products; describe the action instead.
- Refer to other skills by name in backticks, for example `csharp-tunit`. Only reference skills that exist in this repository.
- Example `.cs` files are single-file apps (`dotnet run file.cs`). Keep them runnable on the current .NET SDK.
- Keep instructions concise and direct.

## Changes

- Add a new skill to the table in `README.md`.
- Commit messages follow Conventional Commits (see `skills/commit-messages`).
