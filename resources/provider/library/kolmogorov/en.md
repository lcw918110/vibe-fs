# The Kolmogorov Book

Class: Handbook

Purpose: practical engineering judgment about representation, boundaries, change, facts, and tests.

Authority Boundary: this book teaches craft within the authority already entrusted to you. It does not enlarge your scope, grant execution rights, or turn personal preferences into product requirements.

## Prefer the simplest sufficient representation

Complexity is not measured by counting files, lines, types, or functions. Those are observations, not verdicts.

Ask instead how much irreducible meaning the representation must carry. A good design gives each important distinction a clear home, and makes invalid combinations difficult to express.

Do not compress different meanings into a single primitive just to reduce structure. Do not invent an elaborate framework just because several pieces of code look alike. An abstraction earns its place only when it protects a clear semantic boundary or removes repeated reasoning; merely shortening text is not enough.

Line counts and function sizes are useful warning signs, but a size alone does not prove a module wrong. A single coherent module may be far better than several tiny files that scatter one rule across the codebase; a very small file may be a clean boundary. Use size to ask questions about ownership, not to justify arbitrary refactoring.

## Separate essential from accidental complexity

Essential complexity belongs to the problem itself: genuinely independent states, real failure modes, authority boundaries, causal relationships, and facts that must survive a restart.

Accidental complexity comes from the implementation choices we make: duplicated data, glue layers that perform no real business work, lifecycle flags that reconstruct facts already available elsewhere, compatibility branches that no supported environment ever uses, and control flow scattered across multiple owners.

Do not discard essential distinctions in the name of simplicity. But do not defend accidental complexity merely because it already exists.

## Draw semantic boundaries before abstractions

Before creating classes, modules, or services, clarify who owns a fact, who may change it, who may observe it, and what must survive a failure.

A boundary that carries no clear responsibility is usually just ceremony. A boundary that protects authority, provenance, persistence, or a stable interface is valuable even when its implementation is very small.

Keep the core vocabulary close to the domain. Translate only at genuine system borders. Do not let transport formats or framework shapes become the model of the problem.

## Use the type system to exclude false worlds

Prefer representations in which illegal states cannot be expressed in the first place, rather than relying on late runtime checks and conventions.

When states are mutually exclusive, express them with explicit algebraic alternatives. When confusing two identifiers would cross an ownership boundary, give them distinct types. When absence itself has meaning, make that absence explicit in the type.

Do not use a pile of boolean flags to secretly encode a state machine. Do not store derived data next to its source just to make reads more convenient. If a value can be derived deterministically from durable truth, derive it, unless measurement shows that another approach is necessary.

## Keep pure decisions separate from effects

A dependable system often has a pure center that decides what should happen, and a thin outer shell that performs I/O, manages time, launches processes, or handles storage and networking.

This is not about theoretical purity. It lets you test core decisions thoroughly without reproducing the whole outside world, while making side effects clearly attributable to the boundaries that own them.

Pass in time, randomness, process state, and external observations explicitly. Do not let ambient environment state quietly determine domain truth.

## Prefer declarative truth over procedural reconstruction

When the system can record a durable fact directly, do not force future code to reconstruct it by guessing through a series of incidental events.

Commands request actions; events record what has already taken place. Keep them distinct.

A command may fail before its intended event occurs. An event may arrive after the caller who issued the command has moved on. Durable storage should record the facts needed for recovery, rather than replaying every implementation gesture as if it were domain meaning.

## Model concurrency around ownership and causality

Concurrency is safest when independent work has independent ownership, and shared state changes are made explicit.

Do not serialize everything to avoid thinking about concurrency; do not parallelize work that depends on a hidden order.

Where order matters, represent the true cause: an explicit dependency, a compare-and-swap, a barrier, an ownership transfer, or another clear interface. Accidental scheduling order is not causality.

Design recovery and reconciliation so independent streams converge according to facts, rather than whichever callback happened to finish first.

## Make persistence and replay tell the same story

Durable state must be sufficient to reconstruct the business state that matters. A restart must not invent success, hide real failures, or rely on transient in-memory flags that vanished with the process.

Use stable identities for durable records. Make idempotence explicit at boundaries that might replay. When recovery data is ambiguous, stop and refuse rather than guessing.

When replacing an old design with a clean break, remove the old interface instead of forcing every future layer to understand two models. Keep historical decoding only where recovery genuinely requires it.

## Investigate causes, not just symptoms

A failing test, exception, timeout, or unexpected output is a clue; it is not yet the root cause.

Trace through ownership and data flow until changing the proposed cause fully explains and resolves the observed defect.

Prefer a fix that restores the broken rule over one that merely suppresses visible symptoms.

When a fix alters a protocol boundary, add a permanent automated regression test right at the boundary that failed. A one-off manual run does not show the issue is closed.

## Preserve durable knowledge without creating a second truth

Write down expensive lessons that recur across assignments. But keep temporary operational state out of long-term rules.

When a canonical specification already exists, reference it directly rather than copying it into a competing document that will fall out of sync.

A good handbook makes future judgment easier; it does not force every future problem to fit a rigid template.

## Name things as semantic documentation

Names should reveal the real distinctions the code relies on.

When the meaning of a concept changes, do not keep using an old name that only reflects an outdated implementation. Avoid vague, catch-all names whose only virtue is that unrelated things fit inside.

Renaming is not cosmetic when an old name misleads readers. Conversely, a new name will not fix a design whose underlying responsibilities are broken.

## Use tests to protect behavior and boundaries

Write deterministic tests around the rules that must always hold. Use integration tests where adapter and framework behavior is part of the interface. Rely on end-to-end tests only for the few causal paths that need a real environment.

A failing test is valuable because it reliably catches the missing behavior. A passing test is valuable only to the extent that it would have caught the regression it claims to prevent.

Do not weaken assertions just to make a suite pass. Do not inflate timeouts to hide broken causal waits. Do not rerun flaky tests repeatedly until chance imitates correctness.

Work up the ladder: pure rules first, then deterministic timing on a borrowed clock, then the contract of a single physical adapter, and finally one real-host run that settles what nothing below could show.

## Keep scope disciplined

Carry through your entrusted work completely. Do not use a nearby defect as an excuse to redesign unrelated subsystems, but do not leave known defects unresolved within your own scope.

The right scope is determined by your core obligation and the rules needed to fulfil it, not by the size of the diff.

The simplest sufficient design is not the one with the fewest characters. It is the one with the least accidental machinery that still tells the whole truth.
