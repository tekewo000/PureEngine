---
name: land
description: >-
  Land explicitly requested PureEngine changes on GitHub main and the user's
  primary local main checkout. Invoke only when the user requests landing or
  merging, including /land or acceptance of an offer to run this skill; not for
  review, preparation, passing checks, or skill installation alone.
metadata:
  delta-action: land
---

# Land PureEngine changes

An explicit landing request authorizes this workflow. Proceed without asking
again whether to merge. Installation approval alone does not authorize landing.
Apply this skill only to `tekewo000/PureEngine`; verify repository identity.

## Preflight and scope

- Read applicable AGENT.md/AGENTS.md and current contribution policies/templates.
  Preserve namespace names and declaration styles, English source text, diagnostic
  rules, lifecycle instance methods, and unrelated work as required by AGENTS.md.
  Honor any applicable signing, authorship, review, or submission requirements.
- Inspect status, branches, remotes, and current destination rules before mutation.
  Use `origin` only after verifying it is the GitHub repository above. `local`
  is a backlink to the primary checkout, not a publication remote.
- Identify the requested changes, including uncommitted changes inherited from
  the originating thread. Do not assume this subthread contains later parent edits.
  If necessary inspect the parent with delta-threads. Stop on ambiguous scope.
- Verify Git, authenticated GitHub CLI, PowerShell, and the SDK selected by
  `global.json` (11 RC1, net11.0/C#15; analyzer targets netstandard2.0).
  Do not expose credentials or install tools without permission.
- Resolve the primary checkout from the local remote. The researched location is
  `D:\OtherDevs\PureEngine`. Verify it rather than searching for another copy.
  Inspect its branch, HEAD, and status read-only before changing it.
- Fetch origin and local in the agent workspace. Preserve local-only commits
  relevant to the requested integration. At initial setup local main contained
  `b7856a4` ("Image use Color"), ahead of origin `80252ca`; preserve and integrate
  this change with the Inspector Color implementation, including rendering and
  existing-scene compatibility. Recheck these facts at execution; they are not
  permanent branch requirements.

## Prepare, review, and verify

- Create a unique topic branch in the agent workspace. Stage only reviewed,
  relevant changes with explicit paths and commit non-interactively using
  `git -c core.editor=true commit -m ...`. Keep skill setup and feature work
  separately reviewable; when requested, land the skill first, then the feature.
  Never discard other changes to obtain a clean checkout.
- Integrate current destination history and relevant local work in the agent
  workspace, using non-interactive merge commands. Resolve conflicts automatically
  when intent is clear. Preserve unrelated work; pause for ambiguous intent,
  unsafe resolutions, or scope decisions. Never force-push or rewrite shared history.
- Review behavior and fix defects caused by the changes. Add regression coverage
  appropriate to the changed behavior, and update affected README/design/progress
  documentation. Do not expand into unrelated cleanup.
- For C# or analysis configuration changes, run from the repository root in
  PowerShell: `./tools/code-quality.ps1 -Check`.
  Source: `tools/code-quality.ps1`, `param([switch]$Check)` and its verification
  body; `AGENTS.md` diagnostics section; `.github/workflows/code-quality.yml`.
  It restores, checks style/analyzers at info severity, builds with warnings as
  errors, and runs Core and Editor checks. Check the actual exit status; never
  hide it behind an output-trimming pipeline.
- If bulk fixes are needed, use `./tools/code-quality.ps1`, review its complete
  diff, and manually fix remaining issues. Source: `tools/code-quality.ps1`,
  `if (!$Check)` fix branch and excluded signature/namespace diagnostics.
  Do not weaken rules to pass. Rerun required verification after code changes or
  conflict resolutions; avoid redundant runs without changes or unresolved concerns.
- Docs-only changes are exempt from local quality checks under `AGENTS.md`.
  This exemption does not waive remote checks. Missing SDK/tools or unverifiable
  results are blockers, not permission to substitute an ordinary build.
- Track verification against the exact candidate tree. The initial inherited
  checks do not prove that the integrated Image/Inspector changes work.

## Publish and land

- Push the topic branch to verified origin and create or reuse a GitHub PR targeting
  main with `gh`. Summarize changes, tests, limitations, and applicable issue links
  using current templates. Do not invent issues, CI evidence, or approvals.
- Inspect current protection, rulesets, merge permissions, and required reviews.
  Current policy (2026-09-25): repository ruleset `main-checks` requires a pull
  request and the `check` status from the Code quality workflow; allowed merge
  methods include merge commits, and the repository allows auto-merge. The native
  GitHub merge queue is unavailable because the repository is user-owned (the
  API rejects `merge_queue` rules with 422); do not attempt to create one.
  Do not bypass newly applicable rules or use administrative overrides.
- Wait for the latest candidate's Code quality workflow and every other applicable
  required check/review to succeed before merging. Source:
  `.github/workflows/code-quality.yml` runs job `check` on push, pull_request,
  and merge_group (merge_group stays dormant unless the repository moves to an
  organization), on Windows with SDK selection from global.json and the same
  quality script. Pending, failing, missing, or unverifiable required checks
  block landing. Use bounded waits and inspect actual run/head SHAs and conclusions.
- If the base or candidate changes, integrate safely and obtain verification for
  the updated candidate under current rules before proceeding.
- Enqueue the merge through GitHub auto-merge using a merge commit, matching the
  verified PR head (for example `gh pr merge <number> --auto --merge
  --match-head-commit <sha>`). GitHub merges the PR once required checks pass,
  so concurrent landings serialize without local waiting. If the PR becomes
  conflicted or out of date, update the topic branch safely, re-verify, and
  re-enable auto-merge. Never use `--admin` or other bypasses; stop if the
  destination settings introduce an unresolved decision.
- Fetch origin and verify the PR is merged and its actual merge commit is on
  origin/main. Inspect the destination Code quality run for the resulting commit.
  Enabling auto-merge or pushing the branch is not landing success.

## Update the primary local checkout

- Reinspect the resolved primary checkout immediately before writing. Require
  main and a clean working tree/index, including untracked files that may collide.
  Do not switch a user's branch, stash/reset their work, or overwrite changes.
- Fetch origin there, then use `git -c core.editor=true merge --ff-only origin/main`.
  If local main diverged, preserve it and integrate relevant new commits through
  the reviewed and verified workflow; stop if scope or safety is uncertain.
  Never push directly into its checked-out branch through the local remote.
- Verify primary local main HEAD equals the intended origin/main HEAD, the
  requested changes are present, and status is clean. If remote advances during
  verification, inspect and reconcile safely rather than claiming stale equality.
- If GitHub landed but local synchronization or destination CI failed, report
  partial completion accurately and continue safe recovery when possible.

## Outcome reporting

Report changes, verification, and remaining problems in the conversation.
When in a subthread and report_subthread_status is available, report the result
to the parent as well. Never report skill installation or routine progress as
landing success.

- Use success only after verifying the requested changes reached GitHub main and
  the primary local main, with all applicable checks passed.
- Use failure for genuine blockers or failed attempts, explicitly identifying
  what has and has not landed. Continue permitted safe recovery and report an
  updated outcome after verification.
- Keep title to a few sentence-case words and description to one short line.
  Include a short SHA link and actual CI result link only when verified.
  Example title: "Landed on main"; description: "<verified commit link> ·
  <verified CI passed link>; local main synchronized."
- Questions belong in the conversation, not status events. Do not report clear
  automatically resolved conflicts as failures; pause only on genuine ambiguity.
