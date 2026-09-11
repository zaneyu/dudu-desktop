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
