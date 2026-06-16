# ====================================================================
# Phase AI.C1 -- Frozen survivability vocabularies.
# ====================================================================
#
# This file is dot-sourced from Start-Broker.ps1 in the same place
# the AH frozen vocabularies sit inline (after the broker-restart
# Authority Boundary doctrine block, before function Apply-M1Schema).
# It lives in its own file for ONE reason: the AH.C3 smoke scans
# Start-Broker.ps1 string constants for a reassurance-drift pattern
# whose bare-word list intersects, by design, with the AI.C1
# forbidden-phrase enumeration ($Script:SurvivabilityForbiddenPhrases).
# Keeping the AI declarations OUT OF Start-Broker.ps1 preserves the
# AH.C3 frozen surface without altering it, while still keeping all
# AI.C1 declarations together in one auditable file.
#
# The five arrays declared below are PURE DECLARATIONS in AI.C1.
# They have ZERO consumers in the current slice. AI.C2 wires the
# update-apply / rollback vocabularies into payloads and SQLite
# columns. AI.C3 wires the staged-package-discard vocabulary. AI.C4
# extends the same four-step pattern to the scheduler plane. AI.C6
# extends it to diagnostics. AI.C7 extends it to recovery.
#
# Any future slice that needs to extend a vocabulary MUST extend it
# here. No consumer is permitted to mint a shadow vocabulary; the
# AI.C1 smoke verifies single-source-of-truth across app/broker/**.
#
# ====================================================================
# Four-step lifecycle vocabularies (UpdateLifecyclePhases,
# RollbackLifecyclePhases, StagedPackageDiscardPhases).
# ====================================================================
#
# A single mutation in the survivability plane is observed across
# four DISTINCT moments, each with a different evidence class:
#
#   *_requested
#       The operator's intent has been observed by the broker. Not
#       proof anything has happened. Evidence class: observational.
#
#   *_started / *_attempted
#       The procedure has begun executing. Not proof of completion.
#       Evidence class: observational.
#
#   *_observed_<partial|failed|complete>
#       A first-order observation of what actually happened on the
#       filesystem / process / SQLite. This is NEVER a success
#       claim. "complete" means the procedure ran to its end; it
#       does NOT mean the procedure achieved its goal. Evidence
#       class: observational.
#
#   *_verification_<passed|failed>
#       A SEPARATE check performed AFTER the observed step that
#       directly inspects the authoritative target state (file
#       hashes, package presence, schema rows). This is the only
#       step entitled to make a truth claim about outcome. Evidence
#       class: authoritative.
#
# Forbidden alternatives (rejected by AI.C1 doctrine, gated by
# smoke_ai_c1.ps1 subtest 11 -- terminal-claim regex):
#
#   *_succeeded     terminal-success claim; fuses observed + verified
#   *_completed     terminal-success when applied to a lifecycle phase
#   *_good          theatrical reassurance; carries no evidence
#   *_done          same
#   *_ok            same
#
# The staged-package-discard vocabulary deliberately uses four
# steps with slightly different shape (requested / attempted /
# observed / absence_verified) because the discard procedure is
# AGAINST a target -- the verification is that the package is GONE,
# not that the discard "succeeded".
# ====================================================================

$Script:UpdateLifecyclePhases = @(
    'update_apply_requested',
    'update_apply_started',
    'update_apply_observed_partial',
    'update_apply_observed_failed',
    'update_apply_observed_complete',
    'update_apply_verification_passed',
    'update_apply_verification_failed'
)

$Script:RollbackLifecyclePhases = @(
    'rollback_requested',
    'rollback_started',
    'rollback_observed_partial',
    'rollback_observed_failed',
    'rollback_observed_complete',
    'rollback_verification_passed',
    'rollback_verification_failed'
)

$Script:StagedPackageDiscardPhases = @(
    'staged_package_discard_requested',
    'staged_package_remove_attempted',
    'staged_package_remove_observed',
    'package_absence_verified'
)

# ====================================================================
# Update-apply evaluation events (UpdateEvaluationPhases).
# ====================================================================
#
# Categorically distinct from the four-step lifecycle of a mutation
# event. An evaluation event is a single observation point with NO
# progression -- the operator asks 'could this apply succeed?' and
# the broker records that they asked. There is no _started, no
# _observed_*, no _verification_*; an evaluation request is complete
# the moment it is observed.
#
# This array exists as a SEPARATE category from
# $Script:UpdateLifecyclePhases because forcing evaluation into the
# four-step lifecycle would either break the 4-step pattern or imply
# a 5-step pattern that does not exist. Evaluation is categorically
# not a mutation event.
#
# Evidence class: observational (same as the *_requested step in
# the lifecycle arrays -- the broker observed an operator intent,
# that is all).
#
# Consumed by AI.C2.2: the dryRun preview path of
#   POST /api/v1/updates/apply?dryRun=true
# NEVER consumed by the live (non-dryRun) apply path; the live path
# uses $Script:UpdateLifecyclePhases[0] (update_apply_requested)
# instead. Conflating evaluation with apply intent in a single phase
# would be a wire-format lie and would inflate audit queries that
# count apply attempts. See OPERATOR_GUIDE §17.8 for the
# operator-facing semantic decision.
# ====================================================================

$Script:UpdateEvaluationPhases = @(
    'update_apply_evaluation_requested'
)

# ====================================================================
# Update-request kinds (UpdateRequestKinds).
# ====================================================================
#
# Categorical labels for the two POST /api/v1/updates/apply request
# surfaces. Used by AI.C2.3 as the request_kind value on every
# update_request_observations row. The array is the SINGLE SOURCE OF
# TRUTH for these two strings -- the broker MUST source them by
# index from this array, NEVER hard-code them at a call site.
#
# Index discipline (mirrors AI.C2.1 / AI.C2.2 ordering):
#   [0] update_apply_request             -- live POST (no dryRun query
#                                            string). Pairs with
#                                            $Script:UpdateLifecyclePhases[0]
#                                            (update_apply_requested).
#   [1] update_apply_evaluation_request  -- dryRun POST (?dryRun=true).
#                                            Pairs with
#                                            $Script:UpdateEvaluationPhases[0]
#                                            (update_apply_evaluation_requested).
#
# Doctrine: a request_kind is the categorical label of a wire surface
# that was OBSERVED. It is NOT a lifecycle phase, NOT a status, NOT
# an outcome, NOT an intent. The presence of a request_kind value on
# a persisted row says "the broker observed this kind of request at
# this time". It does NOT say the corresponding operation was
# accepted, queued, deferred, scheduled, started, completed, or
# committed for future execution. Conflating request_kind with apply
# acceptance would re-introduce the very category collapse AI.C2.2
# was designed to prevent.
#
# Future extension: each NEW request surface (e.g. update rollback
# request, package discard request, manifest refresh request) that
# wants to produce persisted observations MUST add its own kind
# string here AND its own persisted-row table; this array MUST NOT
# be reused as a generic catch-all for unrelated routes.
#
# Consumed by AI.C2.3: the dryRun preview branch and the live
# 501 apply_not_yet_implemented branch of Invoke-UpdatesApply.
# See OPERATOR_GUIDE §17.9 for the operator-facing semantic
# decision and §17.8 for the prior evaluation-vs-apply category
# split.
# ====================================================================

$Script:UpdateRequestKinds = @(
    'update_apply_request',
    'update_apply_evaluation_request'
)

# ====================================================================
# Update-apply refusal phases (UpdateRefusalPhases).
# ====================================================================
#
# Canonical refusal-outcome labels for the live (non-dryRun)
# POST /api/v1/updates/apply path. Used by AI.C2.4 as the
# lifecycle_phase value when the broker refuses an apply request
# BEFORE the mutation lifecycle begins -- i.e. before
# $Script:UpdateLifecyclePhases[0] (update_apply_requested) is the
# correct phase to surface.
#
# This array exists as a SEPARATE category from
# $Script:UpdateLifecyclePhases because a refusal is NOT a mutation
# progression step. The chef issued an apply request; the broker
# observed the request and DECLINED to start apply. No mutation
# occurred, no apply began, no apply is owed. Forcing a refusal
# into the seven-step mutation lifecycle would either break the
# seven-step pattern or imply an eight-step pattern that does not
# exist. Refusal is categorically not a mutation event -- it is
# the broker's observed decision to NOT proceed.
#
# Index discipline (mirrors AI.C2.1 / AI.C2.2 / AI.C2.3 ordering):
#   [0] update_apply_refused_reauth_required
#         -- The chef did not complete Windows Hello / PIN
#            re-authentication for the privileged updateApply
#            operation. HTTP 401 with code 'reAuthRequired'.
#   [1] update_apply_refused_active_cooks_present
#         -- One or more active cook records existed in the
#            cook-tracking table at the moment the broker checked
#            preconditions; apply requires zero active cooks.
#            HTTP 409 with error 'update_refused_active_cooks'.
#   [2] update_apply_refused_active_cook_snapshot_failed
#         -- The broker could not determine the active-cook
#            population because the precondition probe itself
#            failed (e.g. database read error). The broker
#            refuses rather than proceed without ground truth.
#            HTTP 503 with error 'active_cook_snapshot_failed'.
#
# Evidence class: observational (same as the *_requested step in
# the lifecycle and evaluation arrays -- the broker observed a
# refusal event, that is all). A persisted refusal row carries NO
# authority: it does NOT say apply was queued, deferred, scheduled,
# retried, resumed, replayed, or owed. It says the chef issued an
# apply request at this time and the broker observed itself
# refusing to proceed. Reading the row at any later time
# (including across restart) yields the same forensic truth.
#
# request_kind pairing: ALL three refusal phases pair with
# $Script:UpdateRequestKinds[0] (update_apply_request) -- the chef
# requested live apply (no ?dryRun=true query string). What
# differs across the three branches is the refusal OUTCOME, not
# the kind of request observed. The evaluation-request kind
# ($Script:UpdateRequestKinds[1]) is never paired with a refusal
# phase because evaluation requests do not have refusal branches
# wired by AI.C2.4; the dryRun preview path always returns HTTP
# 200 and surfaces wouldRefuse as a BODY FIELD, not as a refusal
# event.
#
# Forbidden related vocabulary (rejected by AI.C1 doctrine and
# reaffirmed by AI.C2.4): refusal phases MUST NOT include the
# tokens queued, pending, deferred, retry, continuation, replay,
# resume, scheduled, active_transition, desired_state, or any
# variant suggesting the refused request will be reconsidered.
#
# Consumed by AI.C2.4: the three pre-execution refusal branches
# of Invoke-UpdatesApply -- 401 reAuthRequired, 503
# active_cook_snapshot_failed, 409 update_refused_active_cooks.
# See OPERATOR_GUIDE §17.10 for the operator-facing semantic
# decision and §17.4 for the lifecycle-vs-evaluation-vs-refusal
# categorical split.
# ====================================================================

$Script:UpdateRefusalPhases = @(
    'update_apply_refused_reauth_required',
    'update_apply_refused_active_cooks_present',
    'update_apply_refused_active_cook_snapshot_failed',
    'update_apply_refused_package_trust_mismatch'
)

# ====================================================================
# Survivability evidence classes.
# ====================================================================
#
# Every payload field carrying survivability state MUST be tagged
# with exactly one of these five classes. AH already uses two of
# them on the brokerSession block (runtime-only, observational).
# AI.C2 and later slices add fields tagged as the other three.
#
#   runtime-only
#       Field is computed at process boot and lost on broker exit.
#       Survives no restart boundary.
#       Example: brokerSession.sessionId.
#
#   observational
#       Field is a first-order observation of state at the moment
#       of capture. Carries no authority. May be stale by the time
#       a reader observes it.
#       Example: brokerSession.startupClassification.
#
#   authoritative
#       Field is the result of a verification step that directly
#       inspected the authoritative target state. Read by operators
#       and downstream code as ground truth.
#       Example (future AI.C2): installState.lastUpdateApplyVerification.
#
#   configuration
#       Field reflects on-disk configuration the broker read at
#       startup. Operator-controlled, not broker-controlled.
#       Example (future AI.C3): trust.allowlistPresent.
#
#   historical
#       Field reflects an append-only record of a prior event.
#       Forensic. Never overwritten.
#       Example (future AI.C2): installState.lastUpdateApplyObservation.
#
# Forbidden alternatives (rejected by AI.C1 doctrine):
#
#   inferred           would imply unbounded recursion
#   reconstructed      would imply replay reconstruction
#   synthesized        would imply synthetic confidence
#   confirmed          collapses observation + verification
# ====================================================================

$Script:SurvivabilityEvidenceClasses = @(
    'runtime-only',
    'observational',
    'authoritative',
    'configuration',
    'historical'
)

# ====================================================================
# Survivability forbidden phrases.
# ====================================================================
#
# These 17 phrases are anti-restoration drift words specific to the
# survivability plane (update apply, rollback, scheduler, diagnostics,
# recovery). They EXTEND the AH.C3 reassurance-drift list (which
# guards the runtime plane) without modifying it. The combined check
# is performed by smoke_ai_c1.ps1.
#
# This array is the SINGLE SOURCE OF TRUTH for the phrase list. The
# AI.C1 smoke parses this declaration out of the source and builds
# its regex from the parsed values; OPERATOR_GUIDE section 17.5
# enumerates the same 17 phrases verbatim for operator-facing prose.
# Any future slice that needs to add a phrase MUST add it here
# first; the smoke will fail any doc enumeration that drifts from
# this declaration.
#
# Each phrase is a literal substring (case-insensitive at scan
# time). Phrases are deliberately narrow to avoid false-positive on
# doctrine-affirming negations -- the smoke scans STRING CONSTANTS
# in PowerShell sources (comments exempt) and the non-enumeration
# regions of operator docs.
# ====================================================================

$Script:SurvivabilityForbiddenPhrases = @(
    'auto-repair',
    'self-heal',
    'self-healed',
    'repair completed',
    'successfully recovered',
    'fully restored',
    'automatically recovered',
    'recovered from corruption',
    'silent recovery',
    'transparent recovery',
    'seamlessly resumed',
    'update applied automatically',
    'rolled back successfully',
    'scheduler recovered',
    'task auto-renewed',
    'credential refreshed automatically',
    'package auto-trusted'
)
