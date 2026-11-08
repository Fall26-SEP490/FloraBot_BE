using FloraBot.Api.Modules.KioskOps;

namespace FloraBot.Api.Tests;

public sealed class SlotMaintenancePolicyTests
{
    [Fact]
    public void UnknownActionCannotCompleteALoadingSession()
    {
        var result = SlotMaintenancePolicy.EvaluateStageCompletion((MaintenanceAction)999,
            MaintenanceStage.AwaitLoading, 0, CreateValidCycle(), CreateValidObservation(state: 1), Now);
        Assert.Equal(MaintenanceStage.NeedsInspection, result.NextStage);
        Assert.Equal("UnsupportedMaintenanceAction", result.InspectionReason);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void LoadingRetryRechecksLeaseAndProduct(bool paid, bool active, bool owned)
    {
        var context = CreateValidContext(supportsManualRetry: true) with
        {
            IsLeasePaid = paid,
            IsProductActive = active,
            IsProductOwnedBySeller = owned
        };
        var result = SlotMaintenancePolicy.EvaluateRetry(context, MaintenanceStage.AwaitLoading,
            false, Now.AddSeconds(-30), 1, Now.AddMinutes(1), Now);
        Assert.False(result.Allowed);
        Assert.Equal("LoadingEntitlementRevoked", result.DenialReason);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T10:00:00Z");

    private static SlotMaintenanceContext CreateValidContext(
        string status = "RENTED_EMPTY",
        string zone = "WALK_IN",
        bool isLeasePaid = true,
        bool isProductActive = true,
        bool isProductOwnedBySeller = true,
        bool isActorActive = true,
        bool isShopActive = true,
        bool hasBouquet = false,
        bool supportsManualRetry = false)
    {
        var sellerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var bouquetId = hasBouquet ? Guid.NewGuid() : (Guid?)null;

        return new SlotMaintenanceContext(
            KioskId: kioskId,
            SlotId: slotId,
            SellerId: sellerId,
            ActorId: actorId,
            ActorRole: "SELLER",
            KioskSellerId: sellerId,
            SlotCurrentSellerId: sellerId,
            HasActiveAssignment: true,
            Zone: zone,
            SlotStatus: status,
            BouquetId: bouquetId,
            ProductId: productId,
            IsProductActive: isProductActive,
            IsLeasePaid: isLeasePaid,
            HasActiveCustomerOrder: false,
            HasActiveHold: false,
            HasActiveDoorCommand: false,
            KioskOnline: true,
            LastHeartbeatAt: Now.AddSeconds(-5),
            RelayChannel: 0,
            SensorCapabilityEnabled: true,
            SensorInstalled: true,
            SupportsManualRetry: supportsManualRetry,
            IsActorActive: isActorActive,
            IsShopActive: isShopActive,
            IsProductOwnedBySeller: isProductOwnedBySeller);
    }

    private static SensorObservation CreateValidObservation(
        int relay = 0,
        int state = 0,
        DateTimeOffset? observedAt = null)
    {
        var obsAt = observedAt ?? Now.AddSeconds(-2);
        var states = new int[] { -1, -1, -1, -1 };
        states[relay] = state;
        return new SensorObservation(
            ObservedAt: obsAt,
            ReceivedAt: obsAt.AddMilliseconds(200),
            States: states,
            Installed: true,
            CapabilityEnabled: true);
    }

    private static DoorCycleEvidence CreateValidCycle(
        DateTimeOffset? phaseStartedAt = null,
        DateTimeOffset? doorOpenedAt = null,
        DateTimeOffset? doorClosedAt = null,
        bool isClosed = true)
    {
        var start = phaseStartedAt ?? Now.AddSeconds(-20);
        var open = doorOpenedAt ?? start.AddSeconds(2);
        var close = doorClosedAt ?? open.AddSeconds(5);
        return new DoorCycleEvidence(
            PhaseStartedAt: start,
            DoorOpenedAt: open,
            DoorClosedAt: close,
            IsClosed: isClosed);
    }

    [Fact]
    public void AllowedActionsAndInitialStageMatchStockState()
    {
        Assert.Equal([MaintenanceAction.Load], SlotMaintenancePolicy.GetAllowedActionsForStatus("RENTED_EMPTY"));
        Assert.Equal([MaintenanceAction.Remove, MaintenanceAction.Replace], SlotMaintenancePolicy.GetAllowedActionsForStatus("STOCKED"));
        Assert.Equal([MaintenanceAction.Remove, MaintenanceAction.Replace], SlotMaintenancePolicy.GetAllowedActionsForStatus("PENDING_REMOVAL"));
        Assert.Empty(SlotMaintenancePolicy.GetAllowedActionsForStatus("FREE"));
        Assert.Empty(SlotMaintenancePolicy.GetAllowedActionsForStatus("FAULT"));
        Assert.Empty(SlotMaintenancePolicy.GetAllowedActionsForStatus("LOCKED"));

        Assert.Equal(MaintenanceStage.AwaitLoading, SlotMaintenancePolicy.DetermineInitialStage(MaintenanceAction.Load));
        Assert.Equal(MaintenanceStage.AwaitRemoval, SlotMaintenancePolicy.DetermineInitialStage(MaintenanceAction.Remove));
        Assert.Equal(MaintenanceStage.AwaitRemoval, SlotMaintenancePolicy.DetermineInitialStage(MaintenanceAction.Replace));
    }

    [Fact]
    public void LoadSuccessfulInitialValidation()
    {
        var ctx = CreateValidContext(status: "RENTED_EMPTY", hasBouquet: false);
        var result = SlotMaintenancePolicy.ValidateInitialSession(ctx, MaintenanceAction.Load, Now);

        Assert.True(result.Allowed);
        Assert.Equal(MaintenanceAction.Load, result.Action);
        Assert.Equal(MaintenanceStage.AwaitLoading, result.InitialStage);
        Assert.Null(result.DenialReason);
    }

    [Theory]
    [InlineData("STOCKED")]
    [InlineData("PENDING_REMOVAL")]
    public void RemoveAndReplaceInitialValidationAllowed(string status)
    {
        var ctx = CreateValidContext(status: status, hasBouquet: true);

        var removeRes = SlotMaintenancePolicy.ValidateInitialSession(ctx, MaintenanceAction.Remove, Now);
        Assert.True(removeRes.Allowed);
        Assert.Equal(MaintenanceAction.Remove, removeRes.Action);
        Assert.Equal(MaintenanceStage.AwaitRemoval, removeRes.InitialStage);

        var replaceRes = SlotMaintenancePolicy.ValidateInitialSession(ctx, MaintenanceAction.Replace, Now);
        Assert.True(replaceRes.Allowed);
        Assert.Equal(MaintenanceAction.Replace, replaceRes.Action);
        Assert.Equal(MaintenanceStage.AwaitRemoval, replaceRes.InitialStage);
    }

    [Fact]
    public void TenantMismatchAndRevokedRoleAreDenied()
    {
        var ctx = CreateValidContext();

        Assert.Equal("UnauthorizedRole", SlotMaintenancePolicy.ValidateInitialSession(ctx with { ActorRole = "ADMIN" }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("UnauthorizedRole", SlotMaintenancePolicy.ValidateInitialSession(ctx with { ActorRole = "CUSTOMER" }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SlotNotOwnedBySeller", SlotMaintenancePolicy.ValidateInitialSession(ctx with { SlotCurrentSellerId = Guid.NewGuid() }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("KioskNotOwnedBySeller", SlotMaintenancePolicy.ValidateInitialSession(ctx with { KioskSellerId = Guid.NewGuid() }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SlotAssignmentInactive", SlotMaintenancePolicy.ValidateInitialSession(ctx with { HasActiveAssignment = false }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SlotNotInWalkInZone", SlotMaintenancePolicy.ValidateInitialSession(ctx with { Zone = "PREORDER" }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SlotNotInWalkInZone", SlotMaintenancePolicy.ValidateInitialSession(ctx with { Zone = null }, MaintenanceAction.Load, Now).DenialReason);
    }

    [Fact]
    public void ActiveOrderHoldAndDoorCommandBlockSession()
    {
        var ctx = CreateValidContext();

        Assert.Equal("SlotHasActiveCustomerOrder", SlotMaintenancePolicy.ValidateInitialSession(ctx with { HasActiveCustomerOrder = true }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SlotHasActiveHold", SlotMaintenancePolicy.ValidateInitialSession(ctx with { HasActiveHold = true }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("ActiveDoorCommandInProgress", SlotMaintenancePolicy.ValidateInitialSession(ctx with { HasActiveDoorCommand = true }, MaintenanceAction.Load, Now).DenialReason);
    }

    [Fact]
    public void ExpiredLeaseAllowsRemovalOnlyWhileAssigned()
    {
        var emptyCtx = CreateValidContext(status: "RENTED_EMPTY", isLeasePaid: false, hasBouquet: false);
        var loadRes = SlotMaintenancePolicy.ValidateInitialSession(emptyCtx, MaintenanceAction.Load, Now);
        Assert.False(loadRes.Allowed);
        Assert.Equal("LeaseNotPaidForLoad", loadRes.DenialReason);

        var stockedCtx = CreateValidContext(status: "STOCKED", isLeasePaid: false, hasBouquet: true);
        var replaceRes = SlotMaintenancePolicy.ValidateInitialSession(stockedCtx, MaintenanceAction.Replace, Now);
        Assert.False(replaceRes.Allowed);
        Assert.Equal("LeaseNotPaidForReplace", replaceRes.DenialReason);

        var removeRes = SlotMaintenancePolicy.ValidateInitialSession(stockedCtx, MaintenanceAction.Remove, Now);
        Assert.True(removeRes.Allowed);
        Assert.Equal(MaintenanceAction.Remove, removeRes.Action);
        Assert.Equal(MaintenanceStage.AwaitRemoval, removeRes.InitialStage);
    }

    [Fact]
    public void ActiveProductRequiredForLoadAndReplace()
    {
        var emptyCtx = CreateValidContext(status: "RENTED_EMPTY", isProductActive: false, hasBouquet: false);
        Assert.Equal("ValidActiveProductOwnedBySellerRequiredForLoad", SlotMaintenancePolicy.ValidateInitialSession(emptyCtx, MaintenanceAction.Load, Now).DenialReason);

        var unownedProductCtx = CreateValidContext(status: "RENTED_EMPTY", isProductOwnedBySeller: false, hasBouquet: false);
        Assert.Equal("ValidActiveProductOwnedBySellerRequiredForLoad", SlotMaintenancePolicy.ValidateInitialSession(unownedProductCtx, MaintenanceAction.Load, Now).DenialReason);

        var stockedCtx = CreateValidContext(status: "STOCKED", isProductActive: false, hasBouquet: true);
        Assert.Equal("ValidActiveProductOwnedBySellerRequiredForReplace", SlotMaintenancePolicy.ValidateInitialSession(stockedCtx, MaintenanceAction.Replace, Now).DenialReason);
        Assert.True(SlotMaintenancePolicy.ValidateInitialSession(stockedCtx, MaintenanceAction.Remove, Now).Allowed);
    }

    [Fact]
    public void ActiveActorAndShopRequired()
    {
        var ctx = CreateValidContext();
        Assert.Equal("ActorNotActive", SlotMaintenancePolicy.ValidateInitialSession(ctx with { IsActorActive = false }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("ShopNotActive", SlotMaintenancePolicy.ValidateInitialSession(ctx with { IsShopActive = false }, MaintenanceAction.Load, Now).DenialReason);
    }

    [Fact]
    public void SensorCapabilityAndRelayChannelValidation()
    {
        var ctx = CreateValidContext();
        Assert.Equal("SensorCapabilityDisabled", SlotMaintenancePolicy.ValidateInitialSession(ctx with { SensorCapabilityEnabled = false }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("SensorNotInstalled", SlotMaintenancePolicy.ValidateInitialSession(ctx with { SensorInstalled = false }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("InvalidRelayChannel", SlotMaintenancePolicy.ValidateInitialSession(ctx with { RelayChannel = 4 }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("InvalidRelayChannel", SlotMaintenancePolicy.ValidateInitialSession(ctx with { RelayChannel = -1 }, MaintenanceAction.Load, Now).DenialReason);
    }

    [Fact]
    public void KioskHeartbeatFreshnessValidation()
    {
        var ctx = CreateValidContext();
        Assert.Equal("KioskOffline", SlotMaintenancePolicy.ValidateInitialSession(ctx with { KioskOnline = false }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("KioskHeartbeatStale", SlotMaintenancePolicy.ValidateInitialSession(ctx with { LastHeartbeatAt = Now.AddSeconds(-46) }, MaintenanceAction.Load, Now).DenialReason);
        Assert.Equal("KioskHeartbeatFuture", SlotMaintenancePolicy.ValidateInitialSession(ctx with { LastHeartbeatAt = Now.AddSeconds(6) }, MaintenanceAction.Load, Now).DenialReason);
    }

    [Fact]
    public void TelemetryVerificationRejectsMissingStaleFutureAndMalformed()
    {
        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(null, 0, Now, out var r1));
        Assert.Equal("TelemetryMissing", r1);

        var valid = CreateValidObservation();
        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { CapabilityEnabled = false }, 0, Now, out var r2));
        Assert.Equal("SensorCapabilityDisabled", r2);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { Installed = false }, 0, Now, out var r3));
        Assert.Equal("SensorNotInstalled", r3);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { States = [1, 0, 1] }, 0, Now, out var r4));
        Assert.Equal("MalformedSensorStates", r4);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { States = [1, 0, 2, 1] }, 0, Now, out var r4b));
        Assert.Equal("MalformedSensorStates", r4b);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { States = [1, 0, -2, 1] }, 0, Now, out var r4c));
        Assert.Equal("MalformedSensorStates", r4c);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { ReceivedAt = Now.AddSeconds(-46) }, 0, Now, out var r5));
        Assert.Equal("TelemetryReceivedStale", r5);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { ReceivedAt = Now.AddSeconds(6) }, 0, Now, out var r6));
        Assert.Equal("TelemetryReceivedFuture", r6);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { ObservedAt = Now.AddSeconds(-46) }, 0, Now, out var r7));
        Assert.Equal("TelemetryObservedStale", r7);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { ObservedAt = Now.AddSeconds(6) }, 0, Now, out var r8));
        Assert.Equal("TelemetryObservedFuture", r8);

        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(valid with { ObservedAt = DateTimeOffset.UnixEpoch }, 0, Now, out var r9));
        Assert.Equal("TelemetryObservedEpochOrNegative", r9);

        var badReceived = valid with { ObservedAt = Now.AddSeconds(-2), ReceivedAt = Now.AddSeconds(-8) };
        Assert.False(SlotMaintenancePolicy.VerifyTelemetryFreshness(badReceived, 0, Now, out var r10));
        Assert.Equal("TelemetryReceivedBeforeObserved", r10);
    }

    [Fact]
    public void TerminalCompletedStateIsAbsorbingAndNeverDowngrades()
    {
        var resultOffline = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.Completed,
            0,
            cycle: null,
            observation: null,
            Now,
            kioskOnline: false);
        Assert.Equal(MaintenanceStage.Completed, resultOffline.NextStage);
        Assert.Equal("AlreadyCompleted", resultOffline.Outcome);

        var resultStale = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Load,
            MaintenanceStage.Completed,
            0,
            cycle: CreateValidCycle(),
            observation: CreateValidObservation(observedAt: Now.AddDays(-1)),
            Now,
            kioskOnline: true);
        Assert.Equal(MaintenanceStage.Completed, resultStale.NextStage);
        Assert.Equal("AlreadyCompleted", resultStale.Outcome);
    }

    [Theory]
    [InlineData(MaintenanceAction.Remove, MaintenanceStage.AwaitLoading)]
    [InlineData(MaintenanceAction.Load, MaintenanceStage.AwaitRemoval)]
    public void InvalidActionStageCombinationsFailClosed(MaintenanceAction action, MaintenanceStage stage)
    {
        var cycle = CreateValidCycle();
        var obs = CreateValidObservation(state: 0, observedAt: cycle.DoorClosedAt.AddSeconds(1));
        var result = SlotMaintenancePolicy.EvaluateStageCompletion(action, stage, 0, cycle, obs, Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, result.NextStage);
        Assert.Equal("InvalidActionStageCombination", result.InspectionReason);
        Assert.Equal(stage, result.RetainedIntendedStage);
    }

    [Fact]
    public void CloseOnlyEvidenceNotSufficientWithoutPostCloseObservation()
    {
        var cycle = CreateValidCycle();
        var staleObs = CreateValidObservation(observedAt: cycle.DoorClosedAt.AddSeconds(-1));

        var result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            cycle,
            staleObs,
            Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, result.NextStage);
        Assert.Equal("ObservationNotNewerThanDoorClose", result.InspectionReason);
        Assert.Equal(MaintenanceStage.AwaitRemoval, result.RetainedIntendedStage);

        var unclosedCycle = cycle with { IsClosed = false };
        var freshObs = CreateValidObservation(observedAt: cycle.DoorClosedAt.AddSeconds(2));
        var unclosedResult = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            unclosedCycle,
            freshObs,
            Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, unclosedResult.NextStage);
        Assert.Equal("DoorNotClosed", unclosedResult.InspectionReason);

        var futureCycle = cycle with { DoorClosedAt = Now.AddSeconds(10) };
        var futureResult = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            futureCycle,
            freshObs,
            Now);
        Assert.Equal("DoorCycleFutureTime", futureResult.InspectionReason);

        var epochCycle = cycle with { PhaseStartedAt = DateTimeOffset.UnixEpoch };
        var epochResult = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            epochCycle,
            freshObs,
            Now);
        Assert.Equal("DoorCycleZeroOrEpochTime", epochResult.InspectionReason);
    }

    [Fact]
    public void UnsettledStateNeverTreatedAsEmpty()
    {
        var cycle = CreateValidCycle();
        var unsettledObs = CreateValidObservation(state: -1, observedAt: cycle.DoorClosedAt.AddSeconds(2));

        var result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            cycle,
            unsettledObs,
            Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, result.NextStage);
        Assert.Equal("SensorReadingUnsettledOrDoorOpen", result.InspectionReason);
        Assert.Equal(MaintenanceStage.AwaitRemoval, result.RetainedIntendedStage);
    }

    [Fact]
    public void RemoveFlowCompletesWhenConfirmedEmpty()
    {
        var cycle = CreateValidCycle();
        var emptyObs = CreateValidObservation(state: 0, observedAt: cycle.DoorClosedAt.AddSeconds(2));

        var result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            cycle,
            emptyObs,
            Now);

        Assert.Equal(MaintenanceStage.Completed, result.NextStage);
        Assert.Equal("RemovalConfirmedEmpty", result.Outcome);
        Assert.Null(result.InspectionReason);
    }

    [Fact]
    public void ReplaceIsTwoPhaseAndRequiresEmptyBeforeLoading()
    {
        var cycle1 = CreateValidCycle();
        var emptyObs = CreateValidObservation(state: 0, observedAt: cycle1.DoorClosedAt.AddSeconds(2));

        var phase1Result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Replace,
            MaintenanceStage.AwaitRemoval,
            0,
            cycle1,
            emptyObs,
            Now);

        Assert.Equal(MaintenanceStage.AwaitLoading, phase1Result.NextStage);
        Assert.Equal("RemovalConfirmedEmptyReadyForLoading", phase1Result.Outcome);
        Assert.Equal(MaintenanceStage.AwaitLoading, phase1Result.RetainedIntendedStage);

        var cycle2 = CreateValidCycle(phaseStartedAt: Now.AddSeconds(-10), doorOpenedAt: Now.AddSeconds(-8), doorClosedAt: Now.AddSeconds(-3));
        var fullObs = CreateValidObservation(state: 1, observedAt: cycle2.DoorClosedAt.AddSeconds(1));

        var phase2Result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Replace,
            MaintenanceStage.AwaitLoading,
            0,
            cycle2,
            fullObs,
            Now);

        Assert.Equal(MaintenanceStage.Completed, phase2Result.NextStage);
        Assert.Equal("LoadingConfirmedFlowerPresent", phase2Result.Outcome);
    }

    [Fact]
    public void LoadFlowCompletesWhenConfirmedPresent()
    {
        var cycle = CreateValidCycle();
        var fullObs = CreateValidObservation(state: 1, observedAt: cycle.DoorClosedAt.AddSeconds(2));

        var result = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Load,
            MaintenanceStage.AwaitLoading,
            0,
            cycle,
            fullObs,
            Now);

        Assert.Equal(MaintenanceStage.Completed, result.NextStage);
        Assert.Equal("LoadingConfirmedFlowerPresent", result.Outcome);
    }

    [Fact]
    public void MismatchTriggersInspectionAndAllowsRecoveryOnNewSignedObservation()
    {
        var cycle = CreateValidCycle();
        var stillFullObs = CreateValidObservation(state: 1, observedAt: cycle.DoorClosedAt.AddSeconds(2));

        var inspectionResult = SlotMaintenancePolicy.EvaluateStageCompletion(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            cycle,
            stillFullObs,
            Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, inspectionResult.NextStage);
        Assert.Equal("SlotExpectedEmptyButDetected", inspectionResult.InspectionReason);
        Assert.Equal(MaintenanceStage.AwaitRemoval, inspectionResult.RetainedIntendedStage);

        var staleRecoveryObs = CreateValidObservation(state: 0, observedAt: stillFullObs.ObservedAt);
        var staleRecovery = SlotMaintenancePolicy.EvaluateInspectionRecovery(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            stillFullObs.ObservedAt,
            cycle,
            staleRecoveryObs,
            Now);

        Assert.Equal(MaintenanceStage.NeedsInspection, staleRecovery.NextStage);
        Assert.Equal("ObservationNotNewerThanPriorInspection", staleRecovery.InspectionReason);

        var malformedRecoveryObs = CreateValidObservation(state: 0, observedAt: stillFullObs.ObservedAt.AddSeconds(2)) with
        {
            States = [0, 0, 99, 0]
        };
        var malformedRecovery = SlotMaintenancePolicy.EvaluateInspectionRecovery(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            stillFullObs.ObservedAt,
            cycle,
            malformedRecoveryObs,
            Now);
        Assert.Equal("MalformedSensorStates", malformedRecovery.InspectionReason);

        var genuinelyNewObs = CreateValidObservation(state: 0, observedAt: stillFullObs.ObservedAt.AddSeconds(3));
        var recovered = SlotMaintenancePolicy.EvaluateInspectionRecovery(
            MaintenanceAction.Remove,
            MaintenanceStage.AwaitRemoval,
            0,
            stillFullObs.ObservedAt,
            cycle,
            genuinelyNewObs,
            Now);

        Assert.Equal(MaintenanceStage.Completed, recovered.NextStage);
        Assert.Equal("RemovalConfirmedEmpty", recovered.Outcome);
    }

    [Fact]
    public void ReplaceInspectionRecoversToAwaitLoading()
    {
        var cycle = CreateValidCycle();
        var initialFull = CreateValidObservation(state: 1, observedAt: cycle.DoorClosedAt.AddSeconds(1));
        var genuinelyNewEmpty = CreateValidObservation(state: 0, observedAt: initialFull.ObservedAt.AddSeconds(2));

        var recovered = SlotMaintenancePolicy.EvaluateInspectionRecovery(
            MaintenanceAction.Replace,
            MaintenanceStage.AwaitRemoval,
            0,
            initialFull.ObservedAt,
            cycle,
            genuinelyNewEmpty,
            Now);

        Assert.Equal(MaintenanceStage.AwaitLoading, recovered.NextStage);
        Assert.Equal("RemovalConfirmedEmptyReadyForLoading", recovered.Outcome);
    }

    [Fact]
    public void RetryPolicyRespectsFirmwareCapabilityCooldownCapAndStage()
    {
        var ctxUnsupported = CreateValidContext(supportsManualRetry: false);
        var lastIssued = Now.AddSeconds(-35);
        var expiresAt = Now.AddSeconds(120);

        var unsupported = SlotMaintenancePolicy.EvaluateRetry(
            ctxUnsupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssued,
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(unsupported.Allowed);
        Assert.Equal("ManualRetryNotSupportedByFirmware", unsupported.DenialReason);

        var ctxSupported = ctxUnsupported with { SupportsManualRetry = true };

        var cooldownActive = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-20),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(cooldownActive.Allowed);
        Assert.Equal("RetryCooldownActive", cooldownActive.DenialReason);

        var allowed = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-30),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.True(allowed.Allowed);
        Assert.Equal(2, allowed.NextAttemptNumber);
        Assert.Null(allowed.DenialReason);

        var zeroAttempts = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 0,
            expiresAt,
            Now);
        Assert.False(zeroAttempts.Allowed);
        Assert.Equal("MalformedRetryAttempts", zeroAttempts.DenialReason);

        var negativeAttempts = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: -1,
            expiresAt,
            Now);
        Assert.False(negativeAttempts.Allowed);
        Assert.Equal("MalformedRetryAttempts", negativeAttempts.DenialReason);

        var capExceeded = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 2,
            expiresAt,
            Now);
        Assert.False(capExceeded.Allowed);
        Assert.Equal("MaxRetryAttemptsExceeded", capExceeded.DenialReason);

        var afterOpened = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: true,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(afterOpened.Allowed);
        Assert.Equal("DoorAlreadyOpenedInStage", afterOpened.DenialReason);

        var tokenExpired = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            tokenExpiresAt: Now.AddSeconds(-1),
            Now);
        Assert.False(tokenExpired.Allowed);
        Assert.Equal("TokenExpired", tokenExpired.DenialReason);

        var wrongStage = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported,
            MaintenanceStage.NeedsInspection,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(wrongStage.Allowed);
        Assert.Equal("RetryNotPermittedInStage", wrongStage.DenialReason);

        var offlineKiosk = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { KioskOnline = false },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(offlineKiosk.Allowed);
        Assert.Equal("KioskOffline", offlineKiosk.DenialReason);

        var staleHeartbeat = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { LastHeartbeatAt = Now.AddSeconds(-46) },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(staleHeartbeat.Allowed);
        Assert.Equal("KioskHeartbeatStale", staleHeartbeat.DenialReason);

        var revokedOwner = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { SlotCurrentSellerId = Guid.NewGuid() },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(revokedOwner.Allowed);
        Assert.Equal("SlotOwnershipChanged", revokedOwner.DenialReason);

        var reassignedKiosk = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { KioskSellerId = Guid.NewGuid() },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(reassignedKiosk.Allowed);
        Assert.Equal("KioskOwnershipChanged", reassignedKiosk.DenialReason);

        var revokedAssignment = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { HasActiveAssignment = false },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(revokedAssignment.Allowed);
        Assert.Equal("SlotAssignmentRevoked", revokedAssignment.DenialReason);

        var newOrder = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { HasActiveCustomerOrder = true },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(newOrder.Allowed);
        Assert.Equal("SlotHasActiveCustomerOrder", newOrder.DenialReason);

        var newHold = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { HasActiveHold = true },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.False(newHold.Allowed);
        Assert.Equal("SlotHasActiveHold", newHold.DenialReason);

        // Locked status from own session does NOT block retry if owned and assigned
        var lockedSlot = SlotMaintenancePolicy.EvaluateRetry(
            ctxSupported with { SlotStatus = "LOCKED" },
            MaintenanceStage.AwaitRemoval,
            doorOpenedInCurrentStage: false,
            lastIssuedAt: Now.AddSeconds(-35),
            currentAttempts: 1,
            expiresAt,
            Now);
        Assert.True(lockedSlot.Allowed);
        Assert.Equal(2, lockedSlot.NextAttemptNumber);
    }
}
