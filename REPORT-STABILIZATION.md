# MetaVoiceType Stabilization Report

Date: 2026-08-16  
Target version: 1.3.0  
Settings schema: 5

## Status

The local build, tests, clean onboarding, restart, degraded recovery, Vosk activation, release-decision logic, self-contained publish, Velopack packaging, hosted CI, and hosted release workflow are green. Public release [`v1.3.0`](https://github.com/Metater/MetaVoiceType/releases/tag/v1.3.0) was published from corrected tagged commit `cd60e6b977c0b40ac6531c099d92de4b70b43068`. V1.3 UI/polish work was not resumed during this pass.

The emergency stabilization pass is a PASS. There are no remaining stabilization blockers. It is safe to resume separately scoped V1.3 polish work after this pass ends.

## Root causes and fixes

### Duplicate `pasteHere` / Vosk activation

The English catalog defined one logical paste action with two phrases: `paste recording` and `paste here`. The old resolver flattened those aliases into two `VoiceCommandDefinition` objects with the same persisted ID, `pasteHere`. Vosk validation then built a dictionary keyed by ID and failed before constructing the recognizer.

The failure path was `src/MetaVoiceType/Sessions/ApplicationOrchestrator.cs` (`ResolveDefinitions`) into `src/MetaVoiceType/VoiceCommands/VoskCommandRecognizer.cs` (the old dictionary-based validation). The stable action ID is now `pasteRecording`. A command definition contains one action ID and a list of aliases, and the shared schema builder is used by migration, settings validation, and Vosk. Validation rejects duplicate/empty action IDs, empty or reserved aliases, and aliases shared by different actions, while deduplicating aliases within one action. Vosk builds grammar from the grouped aliases and replaces the active recognizer only after the replacement is successfully constructed.

The regression was reproduced from `main` commit `b6806e86517bc240779fda681a2abbe564ca6993`, which declares application version 1.3.0 and used settings schema 4. Schema 5 is the corrected format; migration accepts the earlier schema-1 through schema-4 layouts used by V1.1/V1.2 and the affected V1.3 development build.

### Deterministic migration

Schema 5 introduces `setupCompletedOnce` and the `pasteRecording` key. `onboardingComplete` and `pasteHere` are compatibility inputs only.

Migration now:

- sorts language and command keys deterministically;
- maps `pasteHere` to `pasteRecording`;
- merges and case-insensitively deduplicates aliases;
- migrates the untouched legacy paste defaults to `paste recording` plus `paste here`;
- preserves a customized legacy phrase instead of replacing it;
- clears obsolete command overrides;
- records prior successful onboarding as `setupCompletedOnce`;
- writes the normalized schema and produces identical output on subsequent loads.

Candidate command settings are validated and activated before becoming the in-memory configuration. If settings persistence fails after a grammar rebuild, the previous grammar is restored.

### Readiness and degraded operation

The shortcut regression originated in `src/MetaVoiceType/UI/ViewModels/MainViewModel.cs`, where startup unconditionally called the global hotkey service after model initialization regardless of onboarding state. Application readiness is now centralized in `src/MetaVoiceType/Core/State/ApplicationReadiness.cs` as `SetupIncomplete`, `Initializing`, `Ready`, or `Degraded`. Before setup has genuinely completed, the app does not register the global hotkey and rejects manual recording, paste/copy, voice commands, custom automations, and recording-event shortcuts without playing acceptance cues.

Setup is persisted as complete only after the microphone, dictation backend, and Vosk recognizer are all ready. A previously completed installation may enter degraded mode when Vosk is unavailable; in that state UI/manual dictation, its global recording toggle, and manual paste/copy remain available. Voice commands, custom automations, and recording-event shortcuts remain disabled. Repairing Vosk transitions the same run back to Ready.

### Parakeet extraction stall

Parakeet V2 and V3 were not hanging in archive extraction. `InstallDictationModelAsync` in `src/MetaVoiceType/UI/ViewModels/MainViewModel.cs` synchronously attempted to install the optional NVIDIA runtime during first-time onboarding, making the extraction screen appear stuck without useful progress.

First-time setup now initializes the required CPU path and never blocks on optional GPU runtime installation. GPU acceleration is eligible only after an installation has completed setup previously and CPU-only mode is off. Archive extraction now reports the current file and per-file byte progress, and activation is presented as a separate stage from download/extraction.

### GitHub Actions release detection

The old `.github/workflows/release.yml` ran `gh release view` for the target tag and treated any nonzero exit as “release missing.” In PowerShell, the expected missing-release stderr handling failed the step before outputs were emitted, and the call did not explicitly provide GitHub CLI authentication. The referenced public run `31923588561` is for the same source commit used for reproduction: build and tests passed, then release-decision step 7 failed and all packaging/release steps were skipped.

The workflow now supplies `GH_TOKEN: ${{ github.token }}` and calls `scripts/Get-ReleaseDecision.ps1`. The script reads the declared version, queries release tags with `gh release list --json tagName --limit 1000`, emits `version`, `tag`, `releaseExists`, and `shouldRelease`, and distinguishes an authentication/query failure from a genuinely absent release. Tests cover an existing tag, missing tag, missing version, and failed/authenticated GitHub CLI query. CI runs those tests before publish, and the release workflow runs them before deciding whether to package.

The first hosted stabilization attempt exposed one additional PowerShell-runner issue: the test intentionally invoked a fake failing `gh`, caught and asserted the expected failure, and printed that all tests passed, but left the native `$LASTEXITCODE` nonzero. GitHub's `pwsh` wrapper propagated that stale code. The successful-test path now clears only that expected fixture code; real uncaught script or GitHub failures still fail loudly. Corrected hosted [CI run `31930205274`](https://github.com/Metater/MetaVoiceType/actions/runs/31930205274) and [release run `31930205297`](https://github.com/Metater/MetaVoiceType/actions/runs/31930205297) both completed successfully.

References: [GitHub CLI `gh release list`](https://cli.github.com/manual/gh_release_list) and [GitHub CLI in Actions authentication](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-github-cli).

## Automated verification

- `dotnet build MetaVoiceType.slnx -c Release --no-restore`: passed, 0 warnings, 0 errors.
- `dotnet test MetaVoiceType.slnx -c Release --no-build --no-restore`: passed, 86 tests, 0 failed, 0 skipped.
- `scripts/Test-ReleaseDecision.ps1`: passed.
- Official `actionlint` v1.7.12 against `.github/workflows/ci.yml` and `.github/workflows/release.yml`: passed.
- The CI workflow's exact self-contained `dotnet publish ... --no-restore` command: passed in an isolated output directory.
- `scripts/package.ps1 -Version 1.3.0 -SkipTests`: passed.
- Velopack verified `VelopackApp.Run()`, created the portable package, full package, and Windows setup bundle.
- `git diff --check`: no whitespace errors; only Git line-ending notices.

Regression coverage includes fresh and interrupted setup, the real Avalonia Continue-button binding for Vosk activation, readiness and hotkey registration, degraded repair, setup-completion persistence, all seven stable built-in action IDs, schema fixtures from legacy through schema 5, deterministic/idempotent restart, exact English paste grammar, malformed/empty/ambiguous commands, first-time GPU bypass, and pre-ready orchestrator action/cue/automation blocking.

## Real clean-install verification

An isolated application-data root was used so the normal profile was not modified:

`artifacts/qa/stabilization-clean-20260816`

The run downloaded, verified, and extracted Vosk English, Silero VAD, and Parakeet V2. Both available microphones produced frames. Parakeet V2 initialized on CPU, Vosk loaded its restricted grammar, and the pipeline self-test passed.

An interrupted first attempt left `setupCompletedOnce=false`; restart returned to onboarding and Ctrl+Space did not start a session. After the corrected path completed, the UI reached Ready and persisted `setupCompletedOnce=true`. The recording toggle then started a session, live Vosk accepted `startRecording`, `stopRecording`, and `pasteRecording`, Parakeet finalized transcripts, and paste completed. A normal restart bypassed onboarding, returned to Ready, and did not change the settings-file SHA-256.

For degraded recovery, the isolated Vosk directory was moved aside recoverably. Restart showed voice commands unavailable and manual dictation ready; voice-command and automation inputs were gated while the recording toggle remained available. Restoring the directory and restarting loaded both Vosk and Parakeet again, returned to Ready, and successfully accepted and pasted another dictated session.

Evidence log: `artifacts/qa/stabilization-clean-20260816/Logs/metavoicetype-20260816.log`.

A second isolated root, `artifacts/qa/stabilization-upgrade-20260816`, was launched with a serialized schema-2 profile containing `onboardingComplete=true`, legacy `pasteHere`, a customized `insert that` paste phrase, and a customized `finish now` stop phrase. The real application migrated it to schema 5, retained setup completion, mapped only the logical key to `pasteRecording`, preserved both customized phrases, activated restricted-grammar Vosk and Parakeet V2, and bypassed onboarding. After a complete process restart, both recognizers initialized again and the settings SHA-256 remained exactly `12BD7DD4CF3365ACF811958B403A48F051F3DA6D1D7F8DD8D01DA9153E5FDAA5`.

Upgrade evidence log: `artifacts/qa/stabilization-upgrade-20260816/Logs/metavoicetype-20260816.log`.

## Release artifacts

The local artifacts are under `artifacts/releases`. The setup package was produced successfully but is unsigned because no signing parameters are configured in the existing packaging flow.

- `MetaVoiceType-1.3.0-full.nupkg` — SHA-256 `4EFD6642E79E130B56D01506CAFE56301753FE2102BE1C8776F3131DB1F297F9`
- `MetaVoiceType-win-Portable.zip` — SHA-256 `2EB4369139616FCA9F9CB02214F337933D9736590A44A1F08E1EED2DA1CABB92`
- `MetaVoiceType-win-Setup.exe` — SHA-256 `CD1BF634E7F8ADA07206BAE7F6888EEA2DA007218242F390EAC50FC93FAD5981`

## Hosted verification and remaining blockers

The release-decision script still fails loudly when `gh` is unavailable or authentication/querying fails. Hosted CI and versioned release automation completed successfully for the corrected commit. The public release is non-draft, non-prerelease, and contains all six expected Velopack metadata/package/setup assets.

There are no remaining blockers for this stabilization pass. The existing package flow does not configure code signing, so GitHub's published installer remains unsigned; this is recorded as a packaging limitation rather than a stabilization failure.
