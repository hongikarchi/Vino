# Pinned source references

`sources.lock.json` is the source of truth for repositories used while designing
Vino. Run `scripts/fetch-references.ps1` to create ignored, detached checkouts
under `.references/`.

The checkouts are not Git remotes of Vino. Updating a pin requires a focused
review, license check, replay tests, and a commit explaining what behavior is
being ported. Do not automatically merge an upstream branch.
