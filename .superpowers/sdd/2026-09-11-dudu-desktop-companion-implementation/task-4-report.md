# Task 4 report

## Status

Implemented reminder recurrence, quiet-hour deferral, missed-event reconciliation, and the durable `ReminderEngine` transaction-before-notification flow in `Dudu.Core`.

The root `global.json` preserves the existing `sdk` object exactly and adds only:

```json
"test": {
  "runner": "Microsoft.Testing.Platform"
}
```

This was the minimal prior build-config correction required for the SDK 10.0.112 test runner.

## TDD commands and outputs

The exact positional command from the brief was run with the pinned absolute executable:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ReminderSchedulerTests
```

Its exact runner-level result was:

```text
Specifying a project for 'dotnet test' should be via '--project'.
exit_code=1
```

Microsoft.Testing.Platform requires the equivalent literal invocation below; this is not an MSBuild substitute:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ReminderSchedulerTests
```

RED output before implementation:

```text
error CS0234: The type or namespace name 'Reminders' does not exist in the namespace 'Dudu.Core'
error CS0246: The type or namespace name 'RecurrenceRule' could not be found
error CS0246: The type or namespace name 'MissedOccurrencePolicy' could not be found
error CS0246: The type or namespace name 'QuietHoursBehavior' could not be found
error CS0246: The type or namespace name 'Reminder' could not be found
Get projects properties with MSBuild didn't execute properly with exit code: 1.
exit_code=1
```

Focused GREEN command/output:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~ReminderSchedulerTests

Test run summary: Passed!
  total: 9
  failed: 0
  succeeded: 9
  skipped: 0
exit_code=0
```

Full Core GREEN command/output:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj

Test run summary: Passed!
  total: 21
  failed: 0
  succeeded: 21
  skipped: 0
exit_code=0
```

## Files

Created:

- `src/Dudu.Core/Models/Reminder.cs`
- `src/Dudu.Core/Reminders/ReminderScheduler.cs`
- `src/Dudu.Core/Reminders/ReminderOccurrencePolicy.cs`
- `src/Dudu.Core/Reminders/ReminderEngine.cs`
- `src/Dudu.Core/Abstractions/IReminderRepository.cs`
- `src/Dudu.Core/Abstractions/IReminderDueSink.cs`
- `tests/Dudu.Core.Tests/Reminders/ReminderSchedulerTests.cs`

Modified:

- `global.json` — added only the Microsoft.Testing.Platform runner setting.

## Self-review

- Recurrence is represented by exactly four rule variants: once, daily, selected weekdays, and interval.
- Stored instants are normalized/returned as UTC; local time zones are used only when calculating calendar boundaries.
- Snoozes take precedence over recurrence.
- Invalid spring-forward local minutes advance minute-by-minute to the first valid minute.
- Ambiguous fall-back local times select the earlier UTC instant.
- Quiet-hour deferral routes through `QuietHoursPolicy.NextAllowedUtc`.
- Reconciliation emits at most one latest occurrence and never emits an interval burst.
- Repository advancement is awaited before any due-sink notification, so a failed transaction produces no presentation event.
- `git diff --check` passed.
- Generated package-lock files from the test restore were not retained; `outputs/` and `work/` were not modified.

## Concerns

- The SDK 10.0.112 Microsoft.Testing.Platform runner rejects the brief’s positional project syntax even after the required `global.json` opt-in; the `--project` form is the supported literal `dotnet test` equivalent and is the form used for GREEN verification.
- The reminder model carries the configured `QuietHours` alongside `QuietHoursBehavior` because the required scheduler signature has no separate quiet-hours argument.

## Commit

Commit: `feat: schedule recurring and missed reminders` (the Task 4 implementation commit containing this report)

## Fix round 1 evidence

### RED

Added focused regressions for exact-boundary daily and interval recurrence, two-step quiet-hour deferral recovery, ambiguous fall-back quiet-hour resolution, repository/sink ordering and failure behavior, and cancellation-token propagation.

Command:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~Reminder
```

Exact failing summary:

```text
failed ...ReminderSchedulerTests.Daily_occurrence_at_exact_now_advances_to_the_next_day
  Expected: 2026-09-12T09:00:00.0000000+00:00
  Actual:   2026-09-11T09:00:00.0000000+00:00
failed ...ReminderSchedulerTests.Interval_occurrence_at_exact_now_advances_by_one_period
  Expected: 2026-09-11T10:00:00.0000000+00:00
  Actual:   2026-09-11T08:00:00.0000000+00:00
failed ...ReminderSchedulerTests.Deferred_occurrence_is_delivered_at_quiet_hours_end_and_then_recurrence_advances
  Assert.Single() Failure: The collection was empty
failed ...ReminderSchedulerTests.Quiet_hours_fall_back_boundary_uses_the_earlier_UTC_instant
  Expected: 2026-11-01T08:00:00.0000000+00:00
  Actual:   2026-11-01T09:00:00.0000000+00:00
... failed with 4 error(s)
exit_code=2
```

The engine regression tests were already passing in this RED run, establishing the missing coverage before the implementation changes.

### GREEN

Focused command/output:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj --filter FullyQualifiedName~Reminder

Test run summary: Passed!
  total: 19
  failed: 0
  succeeded: 19
  skipped: 0
exit_code=0
```

Full Core Release command/output:

```text
/Users/zaneyu/Documents/Codex/2026-09-11/https-www-tiktok-com-andi-o/work/dotnet-sdk/dotnet test --project tests/Dudu.Core.Tests/Dudu.Core.Tests.csproj -c Release

Test run summary: Passed!
  total: 30
  failed: 0
  succeeded: 30
  skipped: 0
exit_code=0
```

### Fixes and review

- Equal `deferredDueUtc == nowUtc` is no longer retained; recurrence advances at exact boundaries.
- Reconciliation recognizes persisted `NextDueUtc` as the pending occurrence, including a quiet-hour-deferred timestamp, before calculating the next recurrence.
- `ReminderEngineTests` verify record-before-notify ordering, zero notifications after repository failure, repository cancellation propagation, and sink cancellation propagation.
- `QuietHoursPolicy` now resolves ambiguous local boundaries to the earlier UTC instant, matching Task 4 recurrence resolution.
- `git diff --check` passed.
- No controller/host integration was added; that remains Task 8 scope.
- Test restore generated `src/Dudu.Core/packages.lock.json` and `tests/Dudu.Core.Tests/packages.lock.json`; neither was staged or committed because the project does not intentionally require them.
