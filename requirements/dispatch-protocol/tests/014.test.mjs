import test from 'node:test'

test('WHAT[dispatch-protocol-014] actual invariant incident retains exact committed or unknown claim truth before invoking the injected fatal capability (GAP-136)', { todo: 'GAP-136: dispatch fatal invariant incident reporting port and mandatory capability injection not yet exposed on DispatchSurface' }, () => {
  // WHAT[dispatch-protocol-014] requires typed dispatch invariant incident to retain durable Pending/PhysicalAccepted truth
  // and invoke composition-injected mandatory fatal capability without direct adapter or fallback.
})

test('WHAT[dispatch-protocol-014] duplicated actual dispatch incident reports and kills once without relabeling an accepted send as unsent (GAP-136)', { todo: 'GAP-136: duplicate incident deduplication and kill-once idempotency require dispatch runtime incident capability harness' }, () => {
  // WHAT[dispatch-protocol-014] specifies that the same invariant incident permits at most one report and kill.
})
