using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dudu.Core.Abstractions;
using Dudu.Core.Models;
using Dudu.Core.Time;
using Dudu.Infrastructure.Data;
using Dudu.Infrastructure.Data.Repositories;
using Dudu.Infrastructure.Remote;
using Dudu.Infrastructure.Security;
using Dudu.Infrastructure.Tests.Crypto;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dudu.Infrastructure.Tests.Remote;

public sealed class RemoteSyncServiceTests
{
    [Fact]
    public async Task Duplicate_poll_is_stored_and_presented_once_then_acked()
    {
        await using var fixture = await RemoteSyncFixture.WithSameEnvelopeReturnedTwiceAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);
        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Single(fixture.Envelopes);
        Assert.Single(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
    }

    [Fact]
    public async Task Concurrent_polls_notify_only_the_atomic_storage_winner()
    {
        await using var fixture = await RemoteSyncFixture.WithSameEnvelopeReturnedTwiceAsync();

        await Task.WhenAll(
            fixture.Service.PollOnceAsync(fixture.CancellationToken),
            fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Single(fixture.Envelopes);
        Assert.Single(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
    }

    [Fact]
    public async Task Received_envelope_remains_visible_and_consumable_after_poll()
    {
        await using var fixture = await RemoteSyncFixture.WithSameEnvelopeReturnedTwiceAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(
            [fixture.MessageId],
            (await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken))
                .Select(envelope => envelope.MessageId));
        Assert.True(await fixture.RealRepository.TryConsumeAsync(
            fixture.MessageId,
            DateTimeOffset.UtcNow,
            fixture.CancellationToken));
        Assert.Empty(await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken));
    }

    [Fact]
    public async Task Partial_registration_is_repaired_before_polling()
    {
        await using var fixture = await RemoteSyncFixture.WithPartialRegistrationAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(1, fixture.Relay.RegisterCallCount);
        Assert.Equal(1, fixture.Relay.PollCallCount);
    }

    [Fact]
    public async Task Ack_is_not_sent_when_local_transaction_fails()
    {
        await using var fixture = await RemoteSyncFixture.WithRepositoryFailureAsync();

        await Assert.ThrowsAsync<RemoteSyncException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Empty(fixture.AcknowledgedIds);
    }

    [Fact]
    public async Task One_failing_envelope_does_not_block_the_rest_of_the_batch()
    {
        // M2: a throwing envelope used to abort the whole foreach and -- since it was never acked
        // -- come back first on every subsequent poll, permanently starving every envelope behind
        // it. The good envelope listed after the failing one must still be stored, notified, and
        // acked in the same poll.
        await using var fixture = await RemoteSyncFixture.WithOneFailingEnvelopeAmongGoodOnesAsync();

        await Assert.ThrowsAsync<RemoteSyncException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Equal([Guid.ParseExact(fixture.MessageId, "D")], fixture.Presentations);
    }

    [Fact]
    public async Task Multiple_ack_failures_of_the_same_type_surface_that_type_not_AggregateException()
    {
        // Review finding: more than one envelope failing in the same batch used to be wrapped
        // in AggregateException, which matches none of RunLoopAsync's typed catches
        // (RelayUnauthorizedException, SecretStoreException, RelayProtocolException,
        // RemoteSyncException) -- so two envelopes whose ack both throw RelayProtocolException
        // would skip MaxDelay() backoff and never set the RelayProtocolError status reason.
        await using var fixture = await RemoteSyncFixture.WithTwoGoodEnvelopesAsync();
        fixture.Relay.AckException = () => new RelayProtocolException("simulated unreadable ack response");

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));
    }

    [Fact]
    public async Task Protocol_and_storage_failures_in_the_same_batch_throw_protocol_and_report_the_storage_failure()
    {
        // M1: only the most significant failure in a batch used to be thrown; every other
        // per-envelope failure (here, the failing envelope's storage exception, wrapped as
        // RemoteSyncException) vanished from diagnostics entirely once a more significant
        // RelayProtocolException also failed in the same batch.
        await using var fixture = await RemoteSyncFixture.WithOneFailingEnvelopeAmongGoodOnesAsync();
        fixture.Relay.AckException = () => new RelayProtocolException("simulated unreadable ack response");

        await Assert.ThrowsAsync<RelayProtocolException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Contains(
            fixture.ReportedErrors,
            error => error.Tag == "remote-sync-envelope" && error.Exception is RemoteSyncException);
    }

    [Fact]
    public async Task Unauthorized_and_protocol_failures_in_the_same_batch_throw_unauthorized_and_report_protocol()
    {
        // M1 mixed precedence: RelayUnauthorizedException outranks RelayProtocolException, so it
        // must be the one thrown (driving RunLoopAsync's NeedsRepair handling) -- but the
        // lower-precedence failure must still reach diagnostics instead of silently vanishing.
        await using var fixture = await RemoteSyncFixture.WithTwoGoodEnvelopesAsync();
        var ackCallCount = 0;
        fixture.Relay.AckException = () =>
        {
            ackCallCount++;
            return ackCallCount == 1
                ? new RelayUnauthorizedException("simulated dead token on first ack")
                : new RelayProtocolException("simulated unreadable ack response");
        };

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Contains(
            fixture.ReportedErrors,
            error => error.Tag == "remote-sync-envelope" && error.Exception is RelayProtocolException);
    }

    [Fact]
    public async Task Reveal_does_not_make_a_network_call_or_persist_plaintext()
    {
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync("private hello");

        var note = await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken);

        Assert.Equal("private hello", note.Text);
        Assert.Equal(0, fixture.Relay.RequestCount);
        Assert.DoesNotContain("private hello", fixture.ReadRawDatabaseText());
    }

    [Fact]
    public async Task Unauthorized_poll_flips_to_needs_repair_and_stops_polling()
    {
        await using var fixture = await RemoteSyncFixture.WithUnauthorizedPollAsync();

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
        Assert.True(fixture.Service.NeedsRepair);
    }

    [Fact]
    public async Task Future_deliver_after_is_stored_but_neither_notified_nor_acked()
    {
        // The relay withholds future-scheduled envelopes on its own clock, but a skewed
        // sender/relay clock can still deliver one early. The desktop must not surface the note
        // before its scheduled time -- and must not ack either, since acking would delete the
        // relay copy before it was ever presented.
        await using var fixture = await RemoteSyncFixture.WithFutureDeliverAfterAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Empty(fixture.Presentations);
        Assert.Empty(fixture.AcknowledgedIds);
        // Stored, but not listed before its deliver-after moment...
        Assert.NotNull(await fixture.RealRepository.GetAsync(fixture.MessageId, fixture.CancellationToken));
        Assert.Empty(await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken));
        // ...and listed once it is due.
        var dueRepository = new RemoteEnvelopeRepository(
            fixture.Database, new ShiftedTimeProvider(TimeSpan.FromHours(2)));
        Assert.Equal(
            [fixture.MessageId],
            (await dueRepository.ListPendingAsync(fixture.CancellationToken))
                .Select(envelope => envelope.MessageId));
    }

    [Fact]
    public async Task Reveal_still_works_for_a_note_kept_unsaved_past_the_30_day_window()
    {
        // Freshness is judged at receipt, not at reveal: a note received today must stay
        // revealable when the user opens it 40 days later.
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync(
            "still here", clockNow: DateTimeOffset.UtcNow.AddDays(40));

        var note = await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken);

        Assert.Equal("still here", note.Text);
    }

    [Fact]
    public async Task Invalid_envelopes_beyond_the_daily_quota_are_acked_but_not_stored()
    {
        var total = RemoteSyncService.InvalidEnvelopeDailyQuota + 2;
        await using var fixture = await RemoteSyncFixture.WithInvalidEnvelopesAsync(total);

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(RemoteSyncService.InvalidEnvelopeDailyQuota, fixture.Envelopes.Count);
        Assert.Equal(total, fixture.AcknowledgedIds.Count);
        Assert.Empty(fixture.Presentations);
        Assert.Equal(
            RemoteSyncService.InvalidEnvelopeDailyQuota,
            fixture.ReportedErrors.Count(error => error.Tag == "remote-sync-decrypt"));
        Assert.Single(fixture.ReportedErrors, error => error.Tag == "remote-sync-invalid-quota");
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("unauthorized")]
    public async Task Revoke_clears_local_registration_when_the_relay_device_is_already_gone(string failure)
    {
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.Relay.DeleteDeviceException = failure == "not-found"
            ? () => new RelayNotFoundException()
            : () => new RelayUnauthorizedException();

        await fixture.Service.RevokeDeviceAsync(fixture.CancellationToken);

        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
        Assert.Equal(PairingAvailability.Offline, fixture.Service.State);
    }

    [Fact]
    public async Task Revoke_keeps_the_encryption_key_unless_explicitly_asked_to_destroy_it()
    {
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync("keep me");
        fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");

        await fixture.Service.RevokeDeviceAsync(fixture.CancellationToken);

        Assert.True(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Equal("keep me", (await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken)).Text);

        await fixture.Service.RevokeDeviceAsync(fixture.CancellationToken, destroyEncryptionKey: true);

        Assert.False(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
    }

    [Fact]
    public async Task Revoke_leaves_local_registration_intact_when_the_relay_is_unavailable()
    {
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.Relay.DeleteDeviceException = () => new RelayUnavailableException("simulated outage");

        await Assert.ThrowsAsync<RelayUnavailableException>(
            () => fixture.Service.RevokeDeviceAsync(fixture.CancellationToken));

        Assert.True(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.True(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
    }

    [Fact]
    public async Task GetState_maps_an_unreadable_secret_to_needs_repair_instead_of_throwing()
    {
        // F2: an unreadable DPAPI blob (e.g. after a Windows password reset invalidates the
        // master key) is a dead end on retry, exactly like a dead bearer token -- it must not
        // surface as an unhandled exception (which would leave ConnectionViewModel.Availability
        // stale) or as a generic probe failure indistinguishable from a healthy offline state.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DeviceId);

        var availability = await fixture.Service.GetStateAsync(fixture.CancellationToken);

        Assert.Equal(PairingAvailability.NeedsRepair, availability);
        Assert.True(fixture.Service.NeedsRepair);
    }

    [Fact]
    public async Task GetActiveSenderSessionCount_degrades_gracefully_when_the_secret_is_unreadable()
    {
        // F2: ConnectionViewModel.RefreshAsync calls GetSessionCountAsync unconditionally right
        // after GetStateAsync, in the same try. If this threw, the whole refresh would throw
        // before ever applying the NeedsRepair availability GetStateAsync already determined, and
        // the Connection page would show a generic error instead.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DeviceId);

        var count = await fixture.Service.GetActiveSenderSessionCountAsync(fixture.CancellationToken);

        Assert.Equal(0, count);
        Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
    }

    [Fact]
    public async Task GetActiveSenderSessionCount_degrades_gracefully_when_only_the_token_is_unreadable()
    {
        // H2: the device-id read (poisoned in the test above) is not the only secret-store read on
        // this path -- GetDeviceAsync authenticates with the bearer token, which the real
        // RelayClient reads through the secret store too. The fake relay client does not model
        // that internal read, so simulate the same failure the real one would surface (a
        // SecretStoreException out of GetDeviceAsync) directly through its exception hook, leaving
        // the device id itself readable, to exercise that second read specifically.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.Relay.GetDeviceException = () => new SecretStoreException(
            "The protected secret could not be decrypted.",
            new InvalidOperationException("simulated unreadable DPAPI blob"));

        var count = await fixture.Service.GetActiveSenderSessionCountAsync(fixture.CancellationToken);

        Assert.Equal(0, count);
        Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
    }

    [Fact]
    public async Task Poll_loop_exits_to_needs_repair_instead_of_retrying_a_permanently_unreadable_secret()
    {
        // H2: an unreadable secret never becomes readable on retry, exactly like a dead bearer
        // token -- the loop must stop the same way it does for a 401 instead of backing off and
        // retrying forever against the same unreadable secret.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DeviceId);

        await fixture.Service.StartAsync(fixture.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => fixture.Service.State == PairingAvailability.NeedsRepair,
                fixture.CancellationToken);
            await WaitUntilAsync(() => !fixture.Service.IsRunning, fixture.CancellationToken);

            Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
            Assert.False(
                fixture.Service.IsRunning,
                "the loop must stop polling once it converges on NeedsRepair, not retry forever");
        }
        finally
        {
            await fixture.Service.StopAsync(fixture.CancellationToken);
        }
    }

    [Fact]
    public async Task ForgetPairingLocally_clears_every_local_secret_even_when_none_of_them_can_be_decrypted()
    {
        // F2: the escape hatch out of an unreadable secret store. RevokeDeviceAsync cannot help
        // here -- it needs a working bearer token to authenticate the relay delete call, and that
        // token is exactly what is unreadable. Poison every secret this touches (matching a fully
        // corrupted DPAPI master key) to prove the local forget still succeeds without ever
        // needing to decrypt any of them: ISecretStore.DeleteAsync only removes the stored file,
        // and the unreadable key is treated as H3's "destroy" case, same as if it had parsed and
        // failed. H4's best-effort relay delete still fires once (the fake relay client, unlike
        // the real one, does not need to decrypt the token to "send" the request) and its failure
        // (irrelevant here, since it does not throw) must never block the rest.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DeviceId);
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DesktopToken);
        fixture.SecretStore.PoisonedKeys.Add(RelaySecretKeys.DesktopTokenStaging);
        fixture.SecretStore.PoisonedKeys.Add(RemoteSyncFixture.DesktopPrivateKeySecretKey);

        await fixture.Service.ForgetPairingLocallyAsync(fixture.CancellationToken);

        Assert.Equal(1, fixture.Relay.RequestCount);
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Equal(PairingAvailability.Offline, fixture.Service.State);
    }

    [Fact]
    public async Task Forget_pairing_keeps_a_readable_key_so_stored_notes_stay_revealable_after_repairing()
    {
        // H3: a readable key means every stored-but-unopened remote note is still decryptable --
        // destroying it for no reason, as the old logic always did, would silently and needlessly
        // turn every one of them into a permanent "cannot finish that" error. Only an actually
        // unreadable key should be destroyed (the poisoned-key test above, and the isolated one
        // below).
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync("still here");
        fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");

        await fixture.Service.ForgetPairingLocallyAsync(fixture.CancellationToken);

        Assert.True(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Equal(
            "still here",
            (await fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken)).Text);
    }

    [Fact]
    public async Task Forget_pairing_destroys_an_unreadable_key_and_purges_the_now_undecryptable_stored_envelope()
    {
        // H3: isolates the key-unreadable path from the registration-marker deletes -- only the
        // key is poisoned, DeviceId/Token stay readable -- to prove specifically that an
        // unreadable key both gets deleted and takes its now-permanently-undecryptable stored,
        // unopened envelope with it, instead of leaving a row Love Notes can never finish opening.
        await using var fixture = await RemoteSyncFixture.WithStoredEncryptedEnvelopeAsync("gone forever");
        fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
        fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
        fixture.SecretStore.PoisonedKeys.Add(RemoteSyncFixture.DesktopPrivateKeySecretKey);

        await fixture.Service.ForgetPairingLocallyAsync(fixture.CancellationToken);

        Assert.False(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Empty(await fixture.RealRepository.ListPendingAsync(fixture.CancellationToken));
    }

    [Fact]
    public async Task Forget_pairing_ignores_a_failed_relay_side_device_delete()
    {
        // H4: the relay-side delete is best-effort so the partner's device eventually stops
        // queueing notes to a dead device, but it must never block the local-only forget path
        // that exists specifically for when the relay itself is unreachable or broken.
        await using var fixture = await RemoteSyncFixture.WithRegistrationAsync();
        fixture.Relay.DeleteDeviceException = () => new RelayUnavailableException("simulated outage");

        await fixture.Service.ForgetPairingLocallyAsync(fixture.CancellationToken);

        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopTokenStaging));
        // The default fixture key is readable, so H3 keeps it -- unaffected by the relay failure.
        Assert.True(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Equal(PairingAvailability.Offline, fixture.Service.State);
    }

    [Fact]
    public async Task Forget_pairing_blocks_behind_an_in_flight_registration_instead_of_racing_it()
    {
        // B1 (blocker): before the fix, ForgetPairingLocallyAsync neither stopped the loop nor
        // took _registrationGate, so a re-registration racing an interleaved forget could leave a
        // device registered on the relay (a fresh DeviceId/Token written mid-forget) with its
        // matching private key deleted moments later by forget's own delete -- silently bricking
        // every future note behind a permanent decrypt failure. Block RegisterAsync mid-call so a
        // registration attempt is provably in flight and holding the gate, then start forget
        // concurrently: it must not proceed until the registration either finishes or is released.
        await using var fixture = await RemoteSyncFixture.WithPartialRegistrationAsync();
        fixture.Relay.BlockRegister = true;

        var pollTask = fixture.Service.PollOnceAsync(fixture.CancellationToken);
        await fixture.Relay.RegisterEntered.Task.WaitAsync(fixture.CancellationToken);

        var forgetTask = fixture.Service.ForgetPairingLocallyAsync(fixture.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), fixture.CancellationToken);
        Assert.False(
            forgetTask.IsCompleted,
            "forget must block behind the in-flight registration's _registrationGate, not race it");

        fixture.Relay.ReleaseRegister.TrySetResult(true);
        await pollTask;
        await forgetTask;

        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
        Assert.False(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        // The registration that won the race used the still-present, readable key, so H3 keeps
        // it -- there is no "registered but key-less" state reachable through this interleaving.
        Assert.True(fixture.SecretStore.Values.ContainsKey(RemoteSyncFixture.DesktopPrivateKeySecretKey));
        Assert.Equal(PairingAvailability.Offline, fixture.Service.State);
    }

    [Fact]
    public async Task Rejected_ack_flips_to_needs_repair()
    {
        // An ack rejected with 401 means the stored bearer token is dead, exactly like a 401 on
        // poll: the service must report NeedsRepair rather than backing off and retrying with the
        // same credential.
        await using var fixture = await RemoteSyncFixture.WithFailingAckAsync();

        await Assert.ThrowsAsync<RelayUnauthorizedException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
        Assert.True(fixture.Service.NeedsRepair);
    }

    [Fact]
    public async Task Oversize_envelope_is_acked_and_skipped_without_blocking_the_page()
    {
        // A poison envelope far larger than any well-formed page member is acked (advanced past)
        // without being stored or presented, while the rest of the page still processes normally.
        await using var fixture = await RemoteSyncFixture.WithOversizeEnvelopeAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Single(fixture.Envelopes);
        Assert.Equal(fixture.MessageId, fixture.Envelopes[0].MessageId);
        Assert.Equal([Guid.ParseExact(fixture.MessageId, "D")], fixture.Presentations);
        Assert.Equal(2, fixture.AcknowledgedIds.Count);
        Assert.Contains(fixture.AcknowledgedIds, id => id == fixture.MessageId);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-oversize");
    }

    [Theory]
    // Review C2: both payloads deserialize cleanly -- System.Text.Json binds JSON null onto a
    // non-nullable string member without complaint. The first is the one named in the review;
    // it happened to survive only because the reaction check ran first and rejected null as an
    // unknown reaction. The second is the one that actually reached payload.Text and threw a
    // NullReferenceException, past the old catch filter and out of the poll loop entirely.
    // Both must be treated as any other undecryptable envelope: kept as ciphertext, never
    // presented, acknowledged so they stop coming back.
    [InlineData("""{"kind":"note","text":null,"reaction":null}""")]
    [InlineData("""{"kind":"note","text":null,"reaction":"none"}""")]
    public async Task Null_payload_fields_are_acked_and_stored_without_plaintext(string rawPayloadJson)
    {
        await using var fixture = await RemoteSyncFixture.WithRawPayloadAsync(rawPayloadJson);

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Single(fixture.Envelopes);
        Assert.Empty(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-decrypt");
    }

    [Fact]
    public async Task Malformed_wire_field_is_acked_instead_of_killing_the_poll()
    {
        // Review I1: BuildStoredEnvelope (the Base64Url decode of the wire fields) used to run
        // outside ProcessEnvelopeAsync's try, so one unparsable field threw straight out of the
        // poll -- and, before the RunLoopAsync fix, out of the background loop as well.
        await using var fixture = await RemoteSyncFixture.WithMalformedCiphertextAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Empty(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-decrypt");
    }

    [Fact]
    public async Task Tampered_ciphertext_is_still_acked_after_narrowing_the_decrypt_catch()
    {
        // F1 regression guard: narrowing ProcessEnvelopeAsync's decrypt catch to specific
        // exception types must not change behavior for a genuinely permanent failure. A
        // bit-flipped (but validly encoded) ciphertext fails AES-GCM authentication with a raw
        // CryptographicException, which the narrowed filter still catches.
        await using var fixture = await RemoteSyncFixture.WithTamperedCiphertextAsync();

        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Empty(fixture.Presentations);
        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-decrypt");
    }

    [Fact]
    public async Task Transient_failure_during_decrypt_leaves_the_note_on_the_relay_for_the_next_sync()
    {
        // F1: the audit's core finding. A failure unrelated to this envelope's content (here
        // injected via a clock hiccup after decrypt itself already succeeded) must NOT be treated
        // as "undecryptable" -- that would ack it away and destroy the note forever. It must
        // propagate instead, leaving the envelope un-acked so the next sync retries and delivers
        // it once the transient condition clears.
        await using var fixture = await RemoteSyncFixture.WithFlakyDecryptAsync();

        await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.Service.PollOnceAsync(fixture.CancellationToken));

        Assert.Empty(fixture.AcknowledgedIds);
        Assert.Empty(fixture.Presentations);

        // The hiccup was a one-off: the retry on the next sync succeeds and delivers the note.
        await fixture.Service.PollOnceAsync(fixture.CancellationToken);

        Assert.Equal(new[] { fixture.MessageId }, fixture.AcknowledgedIds);
        Assert.Equal([Guid.ParseExact(fixture.MessageId, "D")], fixture.Presentations);
        Assert.Single(fixture.Envelopes);
    }

    [Fact]
    public async Task Poll_loop_survives_an_unexpected_exception_instead_of_faulting()
    {
        // Review I1: an exception that is not a RemoteSyncException at all (here, a relay client
        // contract violation) used to fault the loop task. _loopTask then stayed non-null, so
        // StartAsync refused to start a replacement and StopAsync rethrew the stale fault: the
        // service looked started and polled nothing until the app restarted.
        await using var fixture = await RemoteSyncFixture.WithUnexpectedPollExceptionAsync();

        await fixture.Service.StartAsync(fixture.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => fixture.ReportedErrors.Any(error => error.Tag == "remote-sync-loop"),
                fixture.CancellationToken);

            Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-loop");
            Assert.True(fixture.Service.IsRunning, "the loop must still be running after an unexpected exception");
        }
        finally
        {
            // Must not rethrow the loop's exception.
            await fixture.Service.StopAsync(fixture.CancellationToken);
        }

        Assert.False(fixture.Service.IsRunning);
    }

    [Fact]
    public async Task Stop_racing_start_does_not_restart_the_poll_loop()
    {
        await using var fixture = await RemoteSyncFixture.WithBlockingPollAsync();

        await fixture.Service.StartAsync(fixture.CancellationToken);
        await fixture.Relay.PollEntered.Task.WaitAsync(fixture.CancellationToken);

        var stop = fixture.Service.StopAsync(fixture.CancellationToken);
        var racingStart = fixture.Service.StartAsync(fixture.CancellationToken);
        Assert.False(stop.IsCompleted);
        Assert.False(racingStart.IsCompleted);

        fixture.Relay.ReleasePoll.TrySetResult(true);
        await stop;
        await racingStart;

        Assert.False(fixture.Service.IsRunning);
    }

    [Fact]
    public async Task Start_after_dispose_is_rejected()
    {
        await using var fixture = await RemoteSyncFixture.CreateAsync();

        await fixture.Service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fixture.Service.StartAsync(fixture.CancellationToken));
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or ten seconds elapse.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    [Fact]
    public async Task Poll_loop_backs_off_after_an_outage_and_resets_on_the_next_success()
    {
        await using var fixture = await RemoteSyncFixture.WithOneTransientOutageAsync();

        await fixture.Service.StartAsync(fixture.CancellationToken);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (fixture.Relay.PollCallCount < 2 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, fixture.CancellationToken);
            }
        }
        finally
        {
            await fixture.Service.StopAsync(fixture.CancellationToken);
        }

        Assert.True(fixture.Relay.PollCallCount >= 2, "expected the loop to retry after the simulated outage");
        Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-poll");
    }

    // P1: GetStateAsync probes must leave a diagnostic for transient failures instead of
    // silently keeping the last known state, indistinguishable from healthy-offline.
    [Fact]
    public async Task GetState_probe_failures_are_logged_and_keep_last_known_state()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.GetDeviceException = () => new RelayUnavailableException("simulated outage");
            Assert.Equal(PairingAvailability.Offline, await fixture.Service.GetStateAsync(fixture.CancellationToken));

            fixture.Relay.GetDeviceException = () => new RelayProtocolException("simulated bad page");
            Assert.Equal(PairingAvailability.Offline, await fixture.Service.GetStateAsync(fixture.CancellationToken));

            Assert.Equal(PairingStatusReason.RelayProtocolError, fixture.Service.StatusReason);
            Assert.Equal(2, sink.EventIds.Count(id => id == 1005));
            Assert.Contains("RelayUnavailableException", sink.JoinedText, StringComparison.Ordinal);
            Assert.Contains("RelayProtocolException", sink.JoinedText, StringComparison.Ordinal);
        }
    }

    // P1: the terminal-backoff path logs SyncLoopTerminal in addition to the reportError callback.
    [Fact]
    public async Task RunLoop_logs_terminal_state_on_protocol_backoff()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.PollException = () => new RelayProtocolException("simulated unreadable page");

            await fixture.Service.StartAsync(fixture.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => sink.EventIds.Contains(1006),
                    fixture.CancellationToken);

                Assert.Contains(1006, sink.EventIds);
                Assert.Contains("protocol-backoff", sink.JoinedText, StringComparison.Ordinal);
                Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-protocol");
                Assert.Equal(PairingStatusReason.RelayProtocolError, fixture.Service.StatusReason);
            }
            finally
            {
                await fixture.Service.StopAsync(fixture.CancellationToken);
            }
        }
    }

    // Review finding: with two envelopes both failing ack with RelayProtocolException, the
    // loop must still reach the same protocol-backoff path (and RelayProtocolError status
    // reason) as a single failure -- not silently fall through as an unmatched AggregateException.
    [Fact]
    public async Task RunLoop_sets_protocol_status_reason_when_multiple_envelopes_fail_ack_the_same_way()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithTwoGoodEnvelopesFailingAckAndLoggerAsync();
        await using (fixture)
        {
            await fixture.Service.StartAsync(fixture.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => sink.EventIds.Contains(1006),
                    fixture.CancellationToken);

                Assert.Contains(1006, sink.EventIds);
                Assert.Equal(PairingStatusReason.RelayProtocolError, fixture.Service.StatusReason);
            }
            finally
            {
                await fixture.Service.StopAsync(fixture.CancellationToken);
            }
        }
    }

    // P1: a failed iteration that retries logs SyncLoopRetry in addition to reportError.
    [Fact]
    public async Task RunLoop_logs_retry_after_a_transient_failure()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.PollException = () => new RelayUnavailableException("simulated outage");

            await fixture.Service.StartAsync(fixture.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => sink.EventIds.Contains(1007),
                    fixture.CancellationToken);

                Assert.Contains(1007, sink.EventIds);
                Assert.Contains("remote-sync-poll", sink.JoinedText, StringComparison.Ordinal);
                Assert.Contains(fixture.ReportedErrors, error => error.Tag == "remote-sync-poll");
            }
            finally
            {
                await fixture.Service.StopAsync(fixture.CancellationToken);
            }
        }
    }

    // Pre-handoff audit: FailureReporter must be a settable property, not just a value the
    // constructor captured -- production composition sets it AFTER construction (DI resolves
    // the constructor's reportError parameter to null), so a poll-loop failure raised only
    // through the property, never through the constructor argument, must still reach it.
    [Fact]
    public async Task FailureReporter_property_set_after_construction_receives_loop_failures()
    {
        var fixture = await RemoteSyncFixture.CreateAsync();
        await using (fixture)
        {
            var propertyReports = new List<(string Tag, Exception Exception)>();
            fixture.Service.FailureReporter = (tag, exception) => propertyReports.Add((tag, exception));

            fixture.Relay.PollException = () => new RelayUnavailableException("simulated outage");

            await fixture.Service.StartAsync(fixture.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => propertyReports.Any(error => error.Tag == "remote-sync-poll"),
                    fixture.CancellationToken);

                Assert.Contains(propertyReports, error => error.Tag == "remote-sync-poll");
            }
            finally
            {
                await fixture.Service.StopAsync(fixture.CancellationToken);
            }
        }
    }

    // P1: the NeedsRepair exit logs SyncLoopTerminal so the stop is auditable in logs too.
    [Fact]
    public async Task RunLoop_logs_terminal_state_on_needs_repair_exit()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.PollException = () => new RelayUnauthorizedException("simulated dead token");

            await fixture.Service.StartAsync(fixture.CancellationToken);
            try
            {
                await WaitUntilAsync(
                    () => sink.EventIds.Contains(1006),
                    fixture.CancellationToken);

                Assert.Contains(1006, sink.EventIds);
                Assert.Contains("needs-repair", sink.JoinedText, StringComparison.Ordinal);
                Assert.Equal(PairingAvailability.NeedsRepair, fixture.Service.State);
            }
            finally
            {
                await fixture.Service.StopAsync(fixture.CancellationToken);
            }
        }
    }

    // P1: reveal failures must leave a diagnostic that carries only the envelope id and the
    // exception type — the secret note, token, and key material genuinely in scope here must
    // never reach the logs.
    [Fact]
    public async Task Reveal_decrypt_failure_is_logged_without_leaking_secrets()
    {
        const string secretNote = "uniquely-private-phrase-6307";
        const string secretToken = "desktop-token-6307";
        var (fixture, sink) = await RemoteSyncFixture.WithTamperedStoredEnvelopeAndLoggerAsync(secretNote);
        await using (fixture)
        {
            fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes(secretToken);
            fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
            var storedKey = fixture.SecretStore.Values[RemoteSyncFixture.DesktopPrivateKeySecretKey];
            var privateKeyMarker = Convert.ToBase64String(storedKey);

            await Assert.ThrowsAnyAsync<Exception>(
                () => fixture.Service.RevealAsync(fixture.MessageId, fixture.CancellationToken));

            Assert.Contains(1003, sink.EventIds);
            Assert.DoesNotContain(secretNote, sink.JoinedText, StringComparison.Ordinal);
            Assert.DoesNotContain(secretToken, sink.JoinedText, StringComparison.Ordinal);
            Assert.DoesNotContain(privateKeyMarker, sink.JoinedText, StringComparison.Ordinal);
        }
    }

    // P1: pairing-code, disconnect, and revoke transient failures must leave a diagnostic.
    [Fact]
    public async Task CreatePairingCode_failure_is_logged()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.CreatePairingCodeException = () => new RelayUnavailableException("simulated outage");

            await Assert.ThrowsAsync<RelayUnavailableException>(
                () => fixture.Service.CreatePairingCodeAsync(fixture.CancellationToken));

            Assert.Contains(1005, sink.EventIds);
            Assert.Contains("RelayUnavailableException", sink.JoinedText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisconnectSenders_failure_is_logged()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.RotateKeyException = () => new RelayUnavailableException("simulated outage");

            await Assert.ThrowsAsync<RelayUnavailableException>(
                () => fixture.Service.DisconnectSendersAsync(fixture.CancellationToken));

            Assert.Contains(1005, sink.EventIds);
            Assert.Contains("RelayUnavailableException", sink.JoinedText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Revoke_transient_failure_is_logged_and_keeps_registration()
    {
        var (fixture, sink) = await RemoteSyncFixture.WithRegistrationAndLoggerAsync();
        await using (fixture)
        {
            fixture.Relay.DeleteDeviceException = () => new RelayUnavailableException("simulated outage");

            await Assert.ThrowsAsync<RelayUnavailableException>(
                () => fixture.Service.RevokeDeviceAsync(fixture.CancellationToken));

            Assert.Contains(1005, sink.EventIds);
            Assert.True(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DeviceId));
            Assert.True(fixture.SecretStore.Values.ContainsKey(RelaySecretKeys.DesktopToken));
        }
    }

    /// <summary>
    /// Wires a real temp SQLite <see cref="Database"/> (see
    /// tests/Dudu.Infrastructure.Tests/Data/DatabaseTests.cs's DatabaseFixture), an
    /// <see cref="InMemorySecretStore"/> pre-seeded with a genuine ECDH key so
    /// <see cref="CryptoFixture"/>-built envelopes actually decrypt, and a <see cref="FakeRelayClient"/>
    /// that never touches the network, to exercise <see cref="RemoteSyncService"/> end to end.
    /// </summary>
    private sealed class RemoteSyncFixture : IAsyncDisposable
    {
        // Matches Dudu.Infrastructure.Crypto.DesktopKeyService's internal SecretStoreKey constant.
        public const string DesktopPrivateKeySecretKey = "desktop-ecdh-private-v1";

        private readonly string _root;
        private readonly Database _database;

        private RemoteSyncFixture(
            string root,
            Database database,
            RemoteEnvelopeRepository realRepository,
            RecordingRemoteEnvelopeRepository recordingRepository,
            FakeRelayClient relay,
            FakeArrivalSink sink,
            InMemorySecretStore secretStore,
            byte[] recipientPublicKeySpki,
            string messageId,
            RemoteSyncService service,
            List<(string Tag, Exception Exception)> reportedErrors)
        {
            _root = root;
            _database = database;
            RealRepository = realRepository;
            Recording = recordingRepository;
            Relay = relay;
            Sink = sink;
            SecretStore = secretStore;
            RecipientPublicKeySpki = recipientPublicKeySpki;
            MessageId = messageId;
            Service = service;
            ReportedErrors = reportedErrors;
        }

        public RemoteEnvelopeRepository RealRepository { get; }
        public RecordingRemoteEnvelopeRepository Recording { get; }
        public FakeRelayClient Relay { get; }
        public FakeArrivalSink Sink { get; }
        public InMemorySecretStore SecretStore { get; }
        public byte[] RecipientPublicKeySpki { get; }
        public string MessageId { get; }
        public RemoteSyncService Service { get; }
        public DatabaseOptions Options => _database.Options;
        public Database Database => _database;
        public List<(string Tag, Exception Exception)> ReportedErrors { get; }
        public CancellationToken CancellationToken => TestContext.Current.CancellationToken;

        public IReadOnlyList<RemoteEnvelope> Envelopes => Recording.Inserted;
        public IReadOnlyList<Guid> Presentations => Sink.Presentations;
        public IReadOnlyList<string> AcknowledgedIds => Relay.AcknowledgedIds;

        public static async Task<RemoteSyncFixture> WithSameEnvelopeReturnedTwiceAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithRepositoryFailureAsync()
        {
            var fixture = await CreateAsync(useThrowingRepository: true);
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        // M2: the first envelope in the batch always fails to store; the second is perfectly
        // good. Listed first so a regression to whole-batch-aborts-on-first-failure would starve
        // it forever instead of just failing this one poll.
        public static async Task<RemoteSyncFixture> WithOneFailingEnvelopeAmongGoodOnesAsync()
        {
            var failingId = Guid.NewGuid().ToString();
            var fixture = await CreateAsync(
                useThrowingRepository: true,
                throwingRepositoryShouldFail: messageId => messageId == failingId);
            var failing = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "bad one", failingId);
            var good = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "good one", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(failing), ToRelayEnvelope(good)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithPartialRegistrationAsync()
        {
            var fixture = await CreateAsync();
            fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("partial-device");
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithStoredEncryptedEnvelopeAsync(
            string text,
            DateTimeOffset? clockNow = null)
        {
            var fixture = await CreateAsync(clockNow: clockNow);
            var encrypted = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, text, fixture.MessageId);
            var stored = new RemoteEnvelope(
                encrypted.MessageId,
                Base64Url.DecodeFromChars(encrypted.Ciphertext),
                Base64Url.DecodeFromChars(encrypted.EphemeralPublicKey),
                Base64Url.DecodeFromChars(encrypted.Nonce),
                null,
                encrypted.DeliverAfterUtc,
                DateTimeOffset.UtcNow)
            {
                HkdfSalt = Base64Url.DecodeFromChars(encrypted.HkdfSalt),
                CreatedUtc = encrypted.CreatedUtc,
            };
            await fixture.RealRepository.TryInsertAsync(stored, TestContext.Current.CancellationToken);
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithRawPayloadAsync(string rawPayloadJson)
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptRawPayloadFor(
                fixture.RecipientPublicKeySpki,
                Encoding.UTF8.GetBytes(rawPayloadJson),
                fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithRegistrationAsync()
        {
            var fixture = await CreateAsync();
            fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
            fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
            fixture.SecretStore.Values[RelaySecretKeys.DesktopTokenStaging] = Encoding.UTF8.GetBytes("token-2");
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithInvalidEnvelopesAsync(int count)
        {
            var fixture = await CreateAsync();
            fixture.Relay.PollResult = Enumerable.Range(0, count)
                .Select(_ => ToRelayEnvelope(CryptoFixture.EncryptRawPayloadFor(
                    fixture.RecipientPublicKeySpki,
                    Encoding.UTF8.GetBytes("""{"kind":"note","text":null,"reaction":null}"""),
                    Guid.NewGuid().ToString())))
                .ToArray();
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithMalformedCiphertextAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            var wire = ToRelayEnvelope(envelope);
            fixture.Relay.PollResult = [wire with { Ciphertext = "not-base64url!!!" }];
            return fixture;
        }

        // F1: a genuinely tampered (bit-flipped, still valid Base64URL) ciphertext fails AES-GCM
        // authentication -- CryptographicException, raised natively by AesGcm and never wrapped by
        // EnvelopeCrypto -- unlike WithMalformedCiphertextAsync above, which fails the earlier
        // Base64Url decode (EnvelopeValidationException). Both are genuinely permanent failures
        // and must still be acked after narrowing the decrypt catch to specific exception types.
        public static async Task<RemoteSyncFixture> WithTamperedCiphertextAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            var ciphertextAndTag = Base64Url.DecodeFromChars(envelope.Ciphertext);
            ciphertextAndTag[0] ^= 0xFF;
            var wire = ToRelayEnvelope(envelope) with
            {
                Ciphertext = Base64Url.EncodeToString(ciphertextAndTag),
            };
            fixture.Relay.PollResult = [wire];
            return fixture;
        }

        // F1: simulates a transient, non-cryptographic failure (e.g. the class of "SQLite busy,
        // IO, secret store hiccup" the audit named) landing inside ProcessEnvelopeAsync's
        // decrypt/build try, after EnvelopeCrypto.Decrypt itself has already succeeded. The old
        // catch-all treated this identically to a tampered envelope: ack and discard, permanently
        // losing a note that was never actually undecryptable.
        public static async Task<RemoteSyncFixture> WithFlakyDecryptAsync()
        {
            var flakyClock = new FlakyClock(DateTimeOffset.UtcNow);
            var fixture = await CreateAsync(clock: flakyClock);
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            // The first _clock.UtcNow read (EnvelopeCrypto.Decrypt's nowUtc argument) must succeed
            // so decrypt genuinely runs to completion; the second (BuildStoredEnvelope's argument)
            // throws once, simulating a hiccup that has nothing to do with this envelope's content.
            flakyClock.ThrowOnAccessNumber = 2;
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithUnexpectedPollExceptionAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.PollException = () => new InvalidOperationException("unexpected relay-client failure");
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithUnauthorizedPollAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.PollException = () => new RelayUnauthorizedException();
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithFailingAckAsync()
        {
            var fixture = await CreateAsync();
            var envelope = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            fixture.Relay.AckException = () => new RelayUnauthorizedException("simulated dead token on ack");
            return fixture;
        }

        // Two independently-good envelopes so both reach the ack step; the test sets
        // fixture.Relay.AckException itself to control what type both acks fail with.
        public static async Task<RemoteSyncFixture> WithTwoGoodEnvelopesAsync()
        {
            var fixture = await CreateAsync();
            var first = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "one", Guid.NewGuid().ToString());
            var second = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "two", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(first), ToRelayEnvelope(second)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithFutureDeliverAfterAsync()
        {
            var fixture = await CreateAsync();
            var deliverAfterUtc = DateTimeOffset.UtcNow.AddHours(1).ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture);
            var envelope = CryptoFixture.EncryptFor(
                fixture.RecipientPublicKeySpki,
                "not yet",
                fixture.MessageId,
                deliverAfterUtc: deliverAfterUtc);
            fixture.Relay.PollResult = [ToRelayEnvelope(envelope)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithOversizeEnvelopeAsync()
        {
            var fixture = await CreateAsync();
            var good = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "hi there", fixture.MessageId);
            var other = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "too big");
            var oversized = ToRelayEnvelope(other) with
            {
                // Stays below no bound the relay page enforces here (the fake client returns it
                // verbatim) but far above the service's per-envelope cap.
                Ciphertext = new string('A', RemoteSyncService.MaxEnvelopeWireBytes * 2),
            };
            fixture.Relay.PollResult = [oversized, ToRelayEnvelope(good)];
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithOneTransientOutageAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.FailNextPolls(1);
            return fixture;
        }

        public static async Task<RemoteSyncFixture> WithBlockingPollAsync()
        {
            var fixture = await CreateAsync();
            fixture.Relay.BlockPoll = true;
            return fixture;
        }

        public static async Task<(RemoteSyncFixture Fixture, RecordingLoggerSink Sink)> WithRegistrationAndLoggerAsync()
        {
            var sink = new RecordingLoggerSink();
            var fixture = await CreateAsync(logger: new RecordingLogger<RemoteSyncService>(sink));
            fixture.SecretStore.Values[RelaySecretKeys.DeviceId] = Encoding.UTF8.GetBytes("device-1");
            fixture.SecretStore.Values[RelaySecretKeys.DesktopToken] = Encoding.UTF8.GetBytes("token-1");
            return (fixture, sink);
        }

        // Registered fixture with a logger sink, plus two good envelopes whose ack both throw
        // RelayProtocolException -- for observing RunLoopAsync's protocol-backoff path (and
        // resulting status reason) when more than one envelope fails the same way in one poll.
        public static async Task<(RemoteSyncFixture Fixture, RecordingLoggerSink Sink)> WithTwoGoodEnvelopesFailingAckAndLoggerAsync()
        {
            var (fixture, sink) = await WithRegistrationAndLoggerAsync();
            var first = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "one", Guid.NewGuid().ToString());
            var second = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, "two", fixture.MessageId);
            fixture.Relay.PollResult = [ToRelayEnvelope(first), ToRelayEnvelope(second)];
            fixture.Relay.AckException = () => new RelayProtocolException("simulated unreadable ack response");
            return (fixture, sink);
        }

        // P1: stores one envelope carrying noteText whose ciphertext is tampered, so RevealAsync
        // fails to decrypt with the secret note genuinely in scope for the leak assertion.
        public static async Task<(RemoteSyncFixture Fixture, RecordingLoggerSink Sink)> WithTamperedStoredEnvelopeAndLoggerAsync(
            string noteText)
        {
            var sink = new RecordingLoggerSink();
            var fixture = await CreateAsync(logger: new RecordingLogger<RemoteSyncService>(sink));
            var encrypted = CryptoFixture.EncryptFor(fixture.RecipientPublicKeySpki, noteText, fixture.MessageId);
            var tamperedCiphertext = Base64Url.DecodeFromChars(encrypted.Ciphertext);
            tamperedCiphertext[0] ^= 0xFF;
            var stored = new RemoteEnvelope(
                encrypted.MessageId,
                tamperedCiphertext,
                Base64Url.DecodeFromChars(encrypted.EphemeralPublicKey),
                Base64Url.DecodeFromChars(encrypted.Nonce),
                null,
                encrypted.DeliverAfterUtc,
                DateTimeOffset.UtcNow)
            {
                HkdfSalt = Base64Url.DecodeFromChars(encrypted.HkdfSalt),
                CreatedUtc = encrypted.CreatedUtc,
            };
            await fixture.RealRepository.TryInsertAsync(stored, TestContext.Current.CancellationToken);
            return (fixture, sink);
        }

        internal static async Task<RemoteSyncFixture> CreateAsync(
            bool useThrowingRepository = false,
            DateTimeOffset? clockNow = null,
            Microsoft.Extensions.Logging.ILogger<RemoteSyncService>? logger = null,
            IClock? clock = null,
            Func<string, bool>? throwingRepositoryShouldFail = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "dudu-remote-sync-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DatabaseOptions(Path.Combine(root, "dudu.db"), Path.Combine(root, "backups"));
            var database = await Database.OpenAsync(options, TestContext.Current.CancellationToken);
            var realRepository = new RemoteEnvelopeRepository(database);
            var recording = new RecordingRemoteEnvelopeRepository(realRepository);
            IRemoteEnvelopeRepository serviceRepository = useThrowingRepository
                ? new ThrowingRemoteEnvelopeRepository(recording, throwingRepositoryShouldFail)
                : recording;

            using var desktopKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var secretStore = new InMemorySecretStore();
            await secretStore.SetAsync(
                DesktopPrivateKeySecretKey,
                desktopKey.ExportPkcs8PrivateKey(),
                TestContext.Current.CancellationToken);
            var recipientPublicKeySpki = desktopKey.ExportSubjectPublicKeyInfo();

            var keyService = new Dudu.Infrastructure.Crypto.DesktopKeyService(secretStore);
            var relay = new FakeRelayClient();
            var sink = new FakeArrivalSink();
            // Must track real UtcNow (not an arbitrary fixed date): EnvelopeCrypto.Decrypt rejects
            // an envelope whose wire createdUtc is more than 5 minutes ahead of the clock it is
            // given, and CryptoFixture.EncryptFor always stamps createdUtc with the real clock.
            clock ??= new FixedClock(clockNow ?? DateTimeOffset.UtcNow);
            var backoff = new PollBackoff(new FixedFractionRandomSource(0));
            var messageId = Guid.NewGuid().ToString();
            var reportedErrors = new List<(string Tag, Exception Exception)>();

            var service = new RemoteSyncService(
                relay,
                serviceRepository,
                sink,
                secretStore,
                keyService,
                clock,
                backoff,
                (tag, exception) => reportedErrors.Add((tag, exception)),
                logger);

            return new RemoteSyncFixture(
                root, database, realRepository, recording, relay, sink, secretStore, recipientPublicKeySpki, messageId, service,
                reportedErrors);
        }

        private static RelayEnvelope ToRelayEnvelope(Dudu.Infrastructure.Crypto.EncryptedEnvelope envelope) =>
            new(
                envelope.ProtocolVersion,
                envelope.MessageId,
                envelope.CreatedUtc,
                envelope.DeliverAfterUtc,
                envelope.EphemeralPublicKey,
                envelope.HkdfSalt,
                envelope.Nonce,
                envelope.Ciphertext);

        public string ReadRawDatabaseText()
        {
            var text = new StringBuilder();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = Options.DatabasePath + suffix;
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.Latin1);
                    text.Append(reader.ReadToEnd());
                }
            }

            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await _database.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ShiftedTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FixedFractionRandomSource(double fraction) : IRandomSource
    {
        public int Next(int exclusiveMax) => (int)(fraction * exclusiveMax);
    }

    // F1: an IClock whose UtcNow getter throws once, on a chosen 1-based access number, then
    // behaves like FixedClock forever after. Stands in for a transient failure (e.g. "SQLite
    // busy, IO, secret store hiccup") landing partway through ProcessEnvelopeAsync's decrypt/build
    // step, which is otherwise pure in-memory work with no seam a test can fault-inject into.
    private sealed class FlakyClock(DateTimeOffset now) : IClock
    {
        private int _accessCount;

        public int? ThrowOnAccessNumber { get; set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                _accessCount++;
                if (_accessCount == ThrowOnAccessNumber)
                {
                    throw new TimeoutException("simulated transient failure unrelated to envelope content");
                }

                return now;
            }
        }

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // P1: captures every string the service hands to a logger, mirroring the sink in
    // PrivacyBoundaryTests, so the new diagnostics can assert both presence and secrecy.
    private sealed class RecordingLoggerSink
    {
        private readonly List<string> _entries = [];
        private readonly List<int> _eventIds = [];
        private readonly object _sync = new();

        public void Add(string entry)
        {
            lock (_sync)
            {
                _entries.Add(entry);
            }
        }

        public void AddEventId(int eventId)
        {
            lock (_sync)
            {
                _eventIds.Add(eventId);
            }
        }

        public string JoinedText
        {
            get
            {
                lock (_sync)
                {
                    return string.Join('\n', _entries);
                }
            }
        }

        public IReadOnlyList<int> EventIds
        {
            get
            {
                lock (_sync)
                {
                    return [.. _eventIds];
                }
            }
        }
    }

    private sealed class RecordingLogger<T>(RecordingLoggerSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Add(formatter(state, exception));
            sink.AddEventId(eventId.Id);
            if (exception is not null)
            {
                sink.Add(exception.GetType().FullName ?? string.Empty);
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var pair in structuredState)
                {
                    sink.Add(pair.Key + "=" + pair.Value);
                }
            }
        }
    }

    /// <summary>Records every envelope a real repository actually inserted (as opposed to a
    /// no-op duplicate), so tests can assert how many distinct envelopes were stored.</summary>
    private sealed class RecordingRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        private readonly IRemoteEnvelopeRepository _inner;

        public RecordingRemoteEnvelopeRepository(IRemoteEnvelopeRepository inner) => _inner = inner;

        public List<RemoteEnvelope> Inserted { get; } = [];

        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.GetAsync(messageId, cancellationToken);

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(cancellationToken);

        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            _inner.TryInsertAsync(envelope, cancellationToken);

        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.IsProcessedAsync(messageId, cancellationToken);

        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            _inner.TryMarkProcessedAsync(messageId, processedUtc, cancellationToken);

        public async Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken)
        {
            var inserted = await _inner.TryInsertAndMarkProcessedAsync(envelope, processedUtc, cancellationToken);
            if (inserted) Inserted.Add(envelope);
            return inserted;
        }

        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(messageId, cancellationToken);

        public Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) =>
            _inner.PruneExpiredAsync(utcNow, retention, cancellationToken);

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) =>
            _inner.DeleteAllAsync(cancellationToken);
    }

    /// <summary>Always throws from the one member <see cref="RemoteSyncService"/> relies on to
    /// persist a freshly decrypted envelope, simulating a local-transaction failure.</summary>
    private sealed class ThrowingRemoteEnvelopeRepository : IRemoteEnvelopeRepository
    {
        private readonly IRemoteEnvelopeRepository _inner;
        private readonly Func<string, bool> _shouldFail;

        /// <param name="shouldFail">Decides, per message id, whether
        /// <see cref="TryInsertAndMarkProcessedAsync"/> fails. Defaults to failing every call,
        /// matching every pre-existing use of this fake.</param>
        public ThrowingRemoteEnvelopeRepository(IRemoteEnvelopeRepository inner, Func<string, bool>? shouldFail = null)
        {
            _inner = inner;
            _shouldFail = shouldFail ?? (_ => true);
        }

        public Task<RemoteEnvelope?> GetAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.GetAsync(messageId, cancellationToken);

        public Task<IReadOnlyList<RemoteEnvelope>> ListPendingAsync(CancellationToken cancellationToken) =>
            _inner.ListPendingAsync(cancellationToken);

        public Task<bool> TryInsertAsync(RemoteEnvelope envelope, CancellationToken cancellationToken) =>
            _inner.TryInsertAsync(envelope, cancellationToken);

        public Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.IsProcessedAsync(messageId, cancellationToken);

        public Task<bool> TryMarkProcessedAsync(string messageId, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            _inner.TryMarkProcessedAsync(messageId, processedUtc, cancellationToken);

        public Task<bool> TryInsertAndMarkProcessedAsync(
            RemoteEnvelope envelope, DateTimeOffset processedUtc, CancellationToken cancellationToken) =>
            _shouldFail(envelope.MessageId)
                ? throw new InvalidOperationException("simulated repository failure")
                : _inner.TryInsertAndMarkProcessedAsync(envelope, processedUtc, cancellationToken);

        public Task DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(messageId, cancellationToken);

        public Task<int> PruneExpiredAsync(DateTimeOffset utcNow, TimeSpan retention, CancellationToken cancellationToken) =>
            _inner.PruneExpiredAsync(utcNow, retention, cancellationToken);

        public Task<int> DeleteAllAsync(CancellationToken cancellationToken) =>
            _inner.DeleteAllAsync(cancellationToken);
    }

    /// <summary>An <see cref="IRemoteNoteArrivalSink"/> test double recording each notification.
    /// Never records note text or reaction (the interface does not carry them).</summary>
    private sealed class FakeArrivalSink : IRemoteNoteArrivalSink
    {
        public List<Guid> Presentations { get; } = [];

        public Task NotifyAsync(Guid messageId, CancellationToken cancellationToken)
        {
            Presentations.Add(messageId);
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="IRelayClient"/> test double that never touches the network. Ack is
    /// idempotent (per the relay's own contract) so <see cref="AcknowledgedIds"/> records each
    /// distinct message id once regardless of how many times it is acknowledged.</summary>
    private sealed class FakeRelayClient : IRelayClient
    {
        private readonly HashSet<string> _ackedSet = new(StringComparer.Ordinal);
        private int _failuresRemaining;

        public int RequestCount { get; private set; }
        public int PollCallCount { get; private set; }
        public int RegisterCallCount { get; private set; }
        public IReadOnlyList<RelayEnvelope>? PollResult { get; set; }
        public Func<Exception>? PollException { get; set; }
        public Func<Exception>? AckException { get; set; }
        public Func<Exception>? DeleteDeviceException { get; set; }
        // P1 logging tests: failure hooks for the on-demand relay calls.
        public Func<Exception>? GetDeviceException { get; set; }
        public Func<Exception>? CreatePairingCodeException { get; set; }
        public Func<Exception>? RotateKeyException { get; set; }
        public List<string> AcknowledgedIds { get; } = [];
        public TaskCompletionSource<bool> PollEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleasePoll { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockPoll { get; set; }
        // B1 regression seam: lets a test hold EnsureRegisteredAsync mid-registration (and thus
        // mid-_registrationGate) so it can prove a concurrent ForgetPairingLocallyAsync blocks
        // behind the gate instead of racing it.
        public TaskCompletionSource<bool> RegisterEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRegister { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockRegister { get; set; }

        public void FailNextPolls(int count) => _failuresRemaining = count;

        public async Task<RelayRegistrationResult> RegisterAsync(string publicKeySpki, CancellationToken cancellationToken)
        {
            RequestCount++;
            RegisterCallCount++;
            if (BlockRegister)
            {
                RegisterEntered.TrySetResult(true);
                await ReleaseRegister.Task;
            }

            return new RelayRegistrationResult(
                "device-1", "token-1", "ABC123", DateTimeOffset.UtcNow.AddMinutes(10));
        }

        public Task<RelayDeviceInfo> GetDeviceAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            if (GetDeviceException is not null)
            {
                throw GetDeviceException();
            }

            return Task.FromResult(new RelayDeviceInfo(DateTimeOffset.UtcNow, "fingerprint", 0));
        }

        public Task<RelayPairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            if (CreatePairingCodeException is not null)
            {
                throw CreatePairingCodeException();
            }

            return Task.FromResult(new RelayPairingCode("ABC123", DateTimeOffset.UtcNow.AddMinutes(10)));
        }

        public Task<string> RotateKeyAsync(string publicKeySpki, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RotateKeyException is not null)
            {
                throw RotateKeyException();
            }

            return Task.FromResult("rotated-token");
        }

        public Task DeleteDeviceAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            if (DeleteDeviceException is not null)
            {
                throw DeleteDeviceException();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RelayEnvelope>> PollAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            PollCallCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new RelayUnavailableException("simulated outage");
            }

            if (PollException is not null)
            {
                throw PollException();
            }

            return PollCoreAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<RelayEnvelope>> PollCoreAsync(CancellationToken cancellationToken)
        {
            if (BlockPoll)
            {
                PollEntered.TrySetResult(true);
                await ReleasePoll.Task;
            }

            return PollResult ?? (IReadOnlyList<RelayEnvelope>)Array.Empty<RelayEnvelope>();
        }

        public Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (AckException is not null)
            {
                throw AckException();
            }

            if (_ackedSet.Add(messageId))
            {
                AcknowledgedIds.Add(messageId);
            }

            return Task.CompletedTask;
        }
    }
}
