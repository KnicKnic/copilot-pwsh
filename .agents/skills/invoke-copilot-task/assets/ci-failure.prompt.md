---
description: Diagnose a failed CI run — identify the root cause, trace it to a specific code change, and suggest a fix
---

A CI run has failed. Your job is to figure out exactly why and provide an actionable fix.

## Instructions

Use the available tools to:

1. Fetch the failed run details: which workflow, which branch, which commit triggered it
2. Fetch the logs for all failed jobs and steps — focus on the actual error output, not the noise
3. Look up the commit that triggered the run — what changed (diff) relative to the previous passing run
4. Identify the root cause: is it a code bug, a broken test, a missing dependency, a config change, an infrastructure flake, or something else?
5. Write your full diagnosis to `ci-failures/<RUN_ID>.md`

The run ID and repository will be provided in the prompt.

## Output Format

Write the report to `ci-failures/<RUN_ID>.md`:

```
# CI Failure — <Workflow Name> #<RUN_ID>

**Branch:** <branch>  
**Commit:** <short hash> — <commit message>  
**Author:** @<author>  
**Triggered:** <timestamp>  
**Status:** <failed job names>

## What Failed

<The specific step/test/command that failed, with the exact error message>

```
<relevant log lines — trimmed to what matters>
```

## Root Cause

<Clear explanation of why it failed. Be specific — point to the file, line, or config that is the problem.>

**Type:** one of: Code bug / Broken test / Missing dependency / Config error / Infra flake / Test data issue

## The Change That Introduced It

**Commit:** <hash>  
**File(s):** <path(s)>  
<Brief description of what changed and how it connects to the failure>

## Suggested Fix

<Concrete steps or code change to fix the issue. Include a code snippet if applicable.>

## Confidence

**High / Medium / Low** — <one sentence explaining confidence level, e.g. "the stack trace points directly at the changed line">
```
