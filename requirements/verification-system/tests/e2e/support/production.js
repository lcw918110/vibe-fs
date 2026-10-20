/**
 * production.js — the harness's facade onto production renderers.
 *
 * `tests/unit/domain.mjs` exists because crossing the Fable boundary by hand is a silent-failure
 * machine, and the harness needs the same protection for the same reason. It cannot import `domain.mjs`
 * itself: that facade is the contract surface for layer 1–3 tests and is shaped around them.
 *
 * ── the trap this file exists to make unreachable ───────────────────────────
 *
 * Measured while writing the N4 gate. `ForkChildPayload.render` takes an F# list; handed a plain JS
 * array it does not throw — it reads the array as an EMPTY list:
 *
 *   render(new ForkChildAssignment('Do X.', undefined, ['Ship it.']))
 *     → the requirement is silently absent, and the payload is byte-identical to the no-requirement
 *       shape
 *
 * `gate-runtime-key-cases.mjs` had exactly this bug: its reviewer case passed `['Ship it.']`, received
 * a payload with no requirements, and still went green — the declaration it asserts is
 * `[anchor, assignment]`, and both fragments are present in every shape. So the case proved the
 * matcher works while never exercising the path it was written for. Right outcome, wrong reason, zero
 * coverage — the same shape as W4's disconnected verdict feed.
 *
 * The fix is not "remember to call `ofArray`". It is that no caller here touches a raw production
 * function, so the conversion cannot be forgotten. Every list-taking parameter is converted on the way
 * in, and `gate-arch010-cases.mjs` asserts that a plain array reaches the payload.
 *
 * The fable-library directory carries its version, so it is resolved by scanning rather than named.
 * A hardcoded `fable-library-js.4.30.0` was the first draft of this file and would have broken on the
 * next Fable upgrade — or, worse, on this tree, which is at 5.13.0.
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import * as ForkModule from '../../../../../dist/Execution/Delegation/Fork/Surface.js';
import * as SyntheticModule from '../../../../../dist/Foundation/SyntheticTomlSurface.js';

const REPO_ROOT = fileURLToPath(new URL('../../../../..', import.meta.url));

const asInstructionDocument = (body) => {
  const normalized = String(body).replace(/\r\n/g, '\n').replace(/\r/g, '\n').trimEnd();
  return normalized.split('\n').map((line) => (line === '' ? '#' : `# ${line}`)).join('\n') + '\n';
};

const readProviderDocument = (semanticPath) =>
  asInstructionDocument(
    readFileSync(join(REPO_ROOT, 'resources', 'provider', semanticPath, 'en.md'), 'utf8'),
  );

const forkInstructions = ForkModule.instructions('en');

// ── SyntheticToml: the canonical writer ──────────────────────────────────────

export const renderString = (text) => SyntheticModule.renderString(text);
export const comment = (text) => SyntheticModule.comment(text);
export const field = (name, renderedValue) => SyntheticModule.field(name, renderedValue);
export const tableArrayEntry = (name, fields) => SyntheticModule.tableArrayEntry(name, fields);
export const byteCount = (text) => SyntheticModule.byteCount(text);

export const syntheticDocument = (instructions, body) => SyntheticModule.renderDocument(instructions, body);

// ── ForkChildPayload: the forked child's first prompt ─────────────────────────

/** The instruction prose every forked child receives, as JS-native values. */
export const forkBaseInstructions = forkInstructions.Base;
export const forkCommissionerRecordInstruction = forkInstructions.CommissionerRecord;
export const forkAttachmentInstruction = forkInstructions.Attachment;
export const forkRequirementsInstruction = forkInstructions.Requirements;

/** Render a forked child's first prompt by field name. */
export const forkPayload = ({
  assignment,
  parentRecord,
  commissionerRecord,
  attachment,
  originalUserRequirements = [],
  payload,
}) => ForkModule.render('en', {
  Assignment: assignment,
  CommissionerRecord: commissionerRecord ?? parentRecord ?? null,
  Attachment: attachment ?? null,
  RootRequirements: originalUserRequirements,
  Payload: payload ?? null,
});

export const forkRelay = (assignment, commissionerRecord, requirements = [], payload) =>
  ForkModule.render('en', {
    Assignment: assignment,
    CommissionerRecord: commissionerRecord ?? null,
    Attachment: null,
    RootRequirements: requirements,
    Payload: payload ?? null,
  });

/** The anchor a scenario declaration uses: the first base instruction, as it appears rendered. */
export const forkAnchor = () => comment(forkBaseInstructions[0]);

// ── ManagerLifecyclePrompt: GLORY activation / idle ───────────────────────────
// Instruction-only SyntheticToml documents (already comment-prefixed).

export const workActivation = () => readProviderDocument('lifecycle/manager/work-activation');
export const idleEncouragement = () => readProviderDocument('lifecycle/manager/idle-post-t1');
export const idleEncouragementPostT1 = () => readProviderDocument('lifecycle/manager/idle-post-t1');
