Review the git log since the last version tag and update CHANGELOG.md accordingly.

Steps:
1. Read CHANGELOG.md to understand the current state
2. Run `git log` from the last version tag (find the latest `v*` tag) to HEAD, showing commit subjects
3. Categorize each meaningful commit under the appropriate heading (Added, Changed, Fixed, Removed) in the `## [Unreleased]` section
4. Skip merge commits, version bump commits, and trivial changes (typos, formatting)
5. Write concise, user-facing descriptions (not raw commit messages)
6. Do not touch any existing versioned sections below `## [Unreleased]`
7. Show me the diff before saving

If $ARGUMENTS is provided, treat it as additional instructions (e.g. "only include commits from the last week" or "focus on API changes").
