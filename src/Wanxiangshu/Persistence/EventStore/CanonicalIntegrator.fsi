namespace Wanxiangshu.Persistence.EventStore

/// The canonical durable-event integrator. It exposes only factory creation
/// over an explicit rule program; all builder state and internal rule slots
/// remain private to the durable-convergence owner. Every domain rule
/// (Sphinx, JsTransaction, Casebook) lives in its owning module and
/// is injected through `createWithRules` in registration order.
[<RequireQualifiedAccess>]
module CanonicalIntegrator =
    /// Journal-only spine: the structural frontier plus the journal fold.
    val baseRules: IntegrationRule list

    /// Explicit construction seam. `rules` is the complete history program
    /// in registration order and must contain the Structural and Journal rules.
    val createWithRules: rules: IntegrationRule list -> isEventTypeKnown: (string -> bool) -> ICanonicalIntegrator
