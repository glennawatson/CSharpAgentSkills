---
name: dispatching-subagents
description: Use when you have 2+ independent pieces of work with no shared state or sequential dependency — failing tests in different subsystems, separate bugs, parallel investigations. Dispatch one focused subagent per problem domain so they run concurrently and keep your own context clean.
---

# Dispatching subagents

Delegate independent work to subagents with isolated context. You craft exactly what each one needs — they do **not** inherit your session history. This runs problems in parallel and keeps your context free for coordination.

## When to use

Use when **all** of these hold:
- 2+ distinct problems (different files, subsystems, or bugs)
- Each can be understood and solved **without** context from the others
- No shared state — agents won't edit the same files or fight over resources

Don't use when:
- Failures are related (fixing one may fix the rest — investigate together first)
- You don't yet know what's broken (explore first, then dispatch)
- The work is sequential (output of one feeds the next)

## How

1. **Split by problem domain.** One agent per independent area. "Fix auth/session.test" and "Fix billing/invoice.test" — not "fix all the tests."
2. **Dispatch in parallel.** Put all the Agent calls in a **single message** so they run concurrently. (Separate messages run them one at a time.)
3. **Integrate.** Read each summary, check for conflicts, run the full suite yourself, spot-check. Agents can make systematic mistakes — verify, don't trust blindly.

## Anatomy of a good agent prompt

Each prompt is **focused, self-contained, and explicit about its return value**:

- **Scope** — exactly one file/subsystem. Narrow.
- **Context** — paste the actual error messages, test names, failing output. The agent has none of your history.
- **Constraints** — e.g. "Fix the test only, do NOT change production code" or "Do NOT touch unrelated files."
- **Return contract** — "Return: root cause + the exact changes you made + final test output." You need this to integrate.

Example:

```
Fix the 3 failing tests in tests/auth/session.test.ts:

  1. "expires idle session"   — expects status 401, gets 200
  2. "refresh extends expiry" — token TTL not updated
  3. "concurrent refresh"     — race, sometimes double-refreshes

Steps:
  1. Read the test file and understand what each test asserts.
  2. Find the root cause — is it a real bug or a stale test expectation?
  3. Fix it properly. Do NOT just bump timeouts to hide a race — fix the race.
  4. Do NOT change code outside src/auth/.

Return: root cause for each, the diff you applied, and the final test run output.
```

## Common mistakes

- **Too broad** — "fix the tests" → agent gets lost. Give it one file.
- **No context** — "fix the race condition" → agent doesn't know where. Paste the failure.
- **No constraints** — agent refactors half the repo. Fence it in.
- **Vague return** — "fix it" → you can't integrate. Demand a specific summary.
- **Papering over** — agents that "fix" by widening timeouts or loosening assertions. Tell them to find the real cause.

## Verify before you trust

After agents return: review each summary, confirm no two touched the same code, run the whole suite together, and sanity-check a couple of the diffs by hand. Independent fixes usually integrate cleanly — but you only know that once the full bar is green.
