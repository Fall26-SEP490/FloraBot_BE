namespace FloraBot.Api.Modules.KioskOps;

public enum MaintenanceAction { Load, Remove, Replace }
public enum MaintenanceStage { AwaitRemoval, AwaitLoading, NeedsInspection, Completed }

public sealed record SlotMaintenanceContext(
    Guid KioskId,
    Guid SlotId,
    Guid SellerId,
    Guid ActorId,
    string ActorRole,
    Guid? KioskSellerId,
    Guid? SlotCurrentSellerId,
    bool HasActiveAssignment,
    string? Zone,
    string SlotStatus,
    Guid? BouquetId,
    Guid? ProductId,
    bool IsProductActive,
    bool IsLeasePaid,
    bool HasActiveCustomerOrder,
    bool HasActiveHold,
    bool HasActiveDoorCommand,
    bool KioskOnline,
    DateTimeOffset? LastHeartbeatAt,
    int RelayChannel,
    bool SensorCapabilityEnabled,
    bool SensorInstalled,
    bool SupportsManualRetry = false,
    bool IsActorActive = false,
    bool IsShopActive = false,
    bool IsProductOwnedBySeller = false,
    long? RowVersion = null);

public sealed record SensorObservation(
    DateTimeOffset ObservedAt,
    DateTimeOffset ReceivedAt,
    int[]? States,
    bool Installed,
    bool CapabilityEnabled);

public sealed record DoorCycleEvidence(
    DateTimeOffset PhaseStartedAt,
    DateTimeOffset DoorOpenedAt,
    DateTimeOffset DoorClosedAt,
    bool IsClosed);

public sealed record InitialValidationResult(
    bool Allowed,
    MaintenanceAction? Action,
    MaintenanceStage? InitialStage,
    string? DenialReason);

public sealed record StageTransitionResult(
    MaintenanceStage NextStage,
    string Outcome,
    string? InspectionReason = null,
    MaintenanceStage? RetainedIntendedStage = null);

public sealed record RetryDecision(
    bool Allowed,
    int NextAttemptNumber,
    string? DenialReason);

public static class SlotMaintenancePolicy
{
    public const int MaxRetryAttempts = 2;
    public const int RetryCooldownSeconds = 30;
    public const int TelemetryFreshnessSeconds = 45;
    public const int TelemetryMaxFutureSeconds = 5;

    public static IReadOnlyList<MaintenanceAction> GetAllowedActionsForStatus(string slotStatus) =>
        slotStatus switch
        {
            "RENTED_EMPTY" => [MaintenanceAction.Load],
            "STOCKED" or "PENDING_REMOVAL" => [MaintenanceAction.Remove, MaintenanceAction.Replace],
            _ => []
        };

    public static MaintenanceStage DetermineInitialStage(MaintenanceAction action) =>
        action switch
        {
            MaintenanceAction.Load => MaintenanceStage.AwaitLoading,
            MaintenanceAction.Remove or MaintenanceAction.Replace => MaintenanceStage.AwaitRemoval,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

    // Note: Verifying the initial fresh-empty sensor baseline before opening the door for Load
    // remains the explicit responsibility of the caller/persistence transaction layer.
    public static InitialValidationResult ValidateInitialSession(
        SlotMaintenanceContext context, MaintenanceAction requestedAction, DateTimeOffset now)
    {
        if (context.ActorId == Guid.Empty) return new(false, null, null, "InvalidActorId");
        if (context.SellerId == Guid.Empty) return new(false, null, null, "InvalidSellerId");
        if (!context.IsActorActive) return new(false, null, null, "ActorNotActive");
        if (!context.IsShopActive) return new(false, null, null, "ShopNotActive");
        if (context.ActorRole is not ("SELLER" or "SELLER_STAFF")) return new(false, null, null, "UnauthorizedRole");
        if (context.SlotCurrentSellerId != context.SellerId) return new(false, null, null, "SlotNotOwnedBySeller");
        if (context.KioskSellerId != context.SellerId) return new(false, null, null, "KioskNotOwnedBySeller");
        if (!context.HasActiveAssignment) return new(false, null, null, "SlotAssignmentInactive");
        if (context.Zone != "WALK_IN") return new(false, null, null, "SlotNotInWalkInZone");
        if (!context.KioskOnline) return new(false, null, null, "KioskOffline");
        if (context.LastHeartbeatAt is null || now - context.LastHeartbeatAt.Value > TimeSpan.FromSeconds(TelemetryFreshnessSeconds))
            return new(false, null, null, "KioskHeartbeatStale");
        if (context.LastHeartbeatAt.Value > now.AddSeconds(TelemetryMaxFutureSeconds))
            return new(false, null, null, "KioskHeartbeatFuture");
        if (!context.SensorCapabilityEnabled) return new(false, null, null, "SensorCapabilityDisabled");
        if (!context.SensorInstalled) return new(false, null, null, "SensorNotInstalled");
        if (context.RelayChannel is < 0 or > 3) return new(false, null, null, "InvalidRelayChannel");
        if (context.HasActiveCustomerOrder) return new(false, null, null, "SlotHasActiveCustomerOrder");
        if (context.HasActiveHold) return new(false, null, null, "SlotHasActiveHold");
        if (context.HasActiveDoorCommand) return new(false, null, null, "ActiveDoorCommandInProgress");

        return requestedAction switch
        {
            MaintenanceAction.Load => ValidateLoad(context),
            MaintenanceAction.Remove => ValidateRemove(context),
            MaintenanceAction.Replace => ValidateReplace(context),
            _ => new(false, null, null, "UnsupportedMaintenanceAction")
        };
    }

    private static InitialValidationResult ValidateLoad(SlotMaintenanceContext context)
    {
        if (context.SlotStatus != "RENTED_EMPTY") return new(false, null, null, "SlotNotRentedEmptyForLoad");
        if (context.BouquetId is not null) return new(false, null, null, "SlotAlreadyContainsBouquet");
        if (!context.IsLeasePaid) return new(false, null, null, "LeaseNotPaidForLoad");
        if (context.ProductId is null || context.ProductId == Guid.Empty || !context.IsProductActive || !context.IsProductOwnedBySeller)
            return new(false, null, null, "ValidActiveProductOwnedBySellerRequiredForLoad");

        return new(true, MaintenanceAction.Load, MaintenanceStage.AwaitLoading, null);
    }

    private static InitialValidationResult ValidateRemove(SlotMaintenanceContext context)
    {
        if (context.SlotStatus is not ("STOCKED" or "PENDING_REMOVAL"))
            return new(false, null, null, "SlotNotStockedOrPendingRemovalForRemove");
        if (context.BouquetId is null || context.BouquetId == Guid.Empty)
            return new(false, null, null, "SlotMissingBouquetForRemove");

        // Expired lease permitted for removal if slot is still assigned
        return new(true, MaintenanceAction.Remove, MaintenanceStage.AwaitRemoval, null);
    }

    private static InitialValidationResult ValidateReplace(SlotMaintenanceContext context)
    {
        if (context.SlotStatus is not ("STOCKED" or "PENDING_REMOVAL"))
            return new(false, null, null, "SlotNotStockedOrPendingRemovalForReplace");
        if (context.BouquetId is null || context.BouquetId == Guid.Empty)
            return new(false, null, null, "SlotMissingBouquetForReplace");
        if (!context.IsLeasePaid) return new(false, null, null, "LeaseNotPaidForReplace");
        if (context.ProductId is null || context.ProductId == Guid.Empty || !context.IsProductActive || !context.IsProductOwnedBySeller)
            return new(false, null, null, "ValidActiveProductOwnedBySellerRequiredForReplace");

        return new(true, MaintenanceAction.Replace, MaintenanceStage.AwaitRemoval, null);
    }

    public static bool VerifyTelemetryFreshness(
        SensorObservation? observation, int relayChannel, DateTimeOffset now, out string? denialReason)
    {
        if (observation is null) { denialReason = "TelemetryMissing"; return false; }
        if (!observation.CapabilityEnabled) { denialReason = "SensorCapabilityDisabled"; return false; }
        if (!observation.Installed) { denialReason = "SensorNotInstalled"; return false; }
        if (relayChannel is < 0 or > 3) { denialReason = "InvalidRelayChannel"; return false; }
        if (observation.States is not { Length: 4 } || !observation.States.All(s => s is >= -1 and <= 1))
        {
            denialReason = "MalformedSensorStates"; return false;
        }
        if (observation.ObservedAt <= DateTimeOffset.UnixEpoch)
        {
            denialReason = "TelemetryObservedEpochOrNegative"; return false;
        }
        if (observation.ReceivedAt < now.AddSeconds(-TelemetryFreshnessSeconds))
        {
            denialReason = "TelemetryReceivedStale"; return false;
        }
        if (observation.ReceivedAt > now.AddSeconds(TelemetryMaxFutureSeconds))
        {
            denialReason = "TelemetryReceivedFuture"; return false;
        }
        if (observation.ObservedAt < now.AddSeconds(-TelemetryFreshnessSeconds))
        {
            denialReason = "TelemetryObservedStale"; return false;
        }
        if (observation.ObservedAt > now.AddSeconds(TelemetryMaxFutureSeconds))
        {
            denialReason = "TelemetryObservedFuture"; return false;
        }
        if (observation.ReceivedAt < observation.ObservedAt.AddSeconds(-TelemetryMaxFutureSeconds))
        {
            denialReason = "TelemetryReceivedBeforeObserved"; return false;
        }

        denialReason = null;
        return true;
    }

    public static bool VerifyClosedDoorCycle(
        DoorCycleEvidence? cycle, SensorObservation observation, DateTimeOffset now, out string? denialReason)
    {
        if (cycle is null) { denialReason = "MissingDoorCycleEvidence"; return false; }
        if (cycle.PhaseStartedAt <= DateTimeOffset.UnixEpoch || cycle.DoorOpenedAt <= DateTimeOffset.UnixEpoch || cycle.DoorClosedAt <= DateTimeOffset.UnixEpoch)
        {
            denialReason = "DoorCycleZeroOrEpochTime"; return false;
        }
        if (cycle.PhaseStartedAt > now.AddSeconds(TelemetryMaxFutureSeconds) ||
            cycle.DoorOpenedAt > now.AddSeconds(TelemetryMaxFutureSeconds) ||
            cycle.DoorClosedAt > now.AddSeconds(TelemetryMaxFutureSeconds))
        {
            denialReason = "DoorCycleFutureTime"; return false;
        }
        if (cycle.DoorOpenedAt < cycle.PhaseStartedAt) { denialReason = "DoorOpenedBeforePhaseStart"; return false; }
        if (cycle.DoorClosedAt < cycle.DoorOpenedAt) { denialReason = "DoorClosedBeforeDoorOpened"; return false; }
        if (!cycle.IsClosed) { denialReason = "DoorNotClosed"; return false; }
        if (observation.ObservedAt <= cycle.PhaseStartedAt) { denialReason = "ObservationNotNewerThanPhaseStart"; return false; }
        if (observation.ObservedAt <= cycle.DoorClosedAt) { denialReason = "ObservationNotNewerThanDoorClose"; return false; }

        denialReason = null;
        return true;
    }

    public static StageTransitionResult EvaluateStageCompletion(
        MaintenanceAction action,
        MaintenanceStage currentStage,
        int relayChannel,
        DoorCycleEvidence? cycle,
        SensorObservation? observation,
        DateTimeOffset now,
        bool kioskOnline = true)
    {
        if (currentStage == MaintenanceStage.Completed)
            return new(MaintenanceStage.Completed, "AlreadyCompleted");

        if (!Enum.IsDefined(action))
            return NeedsInspection("UnsupportedMaintenanceAction", currentStage);

        if (action == MaintenanceAction.Remove && currentStage != MaintenanceStage.AwaitRemoval)
            return NeedsInspection("InvalidActionStageCombination", currentStage);
        if (action == MaintenanceAction.Load && currentStage != MaintenanceStage.AwaitLoading)
            return NeedsInspection("InvalidActionStageCombination", currentStage);
        if (action == MaintenanceAction.Replace && currentStage is not (MaintenanceStage.AwaitRemoval or MaintenanceStage.AwaitLoading))
            return NeedsInspection("InvalidActionStageCombination", currentStage);

        if (!kioskOnline)
            return NeedsInspection("KioskOffline", currentStage);

        if (!VerifyTelemetryFreshness(observation, relayChannel, now, out var telemetryReason))
            return NeedsInspection(telemetryReason!, currentStage);

        if (!VerifyClosedDoorCycle(cycle, observation!, now, out var cycleReason))
            return NeedsInspection(cycleReason!, currentStage);

        int state = observation!.States![relayChannel];
        if (state == -1)
            return NeedsInspection("SensorReadingUnsettledOrDoorOpen", currentStage);

        return currentStage switch
        {
            MaintenanceStage.AwaitRemoval => state == 0
                ? action switch
                {
                    MaintenanceAction.Remove => new(MaintenanceStage.Completed, "RemovalConfirmedEmpty"),
                    MaintenanceAction.Replace => new(MaintenanceStage.AwaitLoading, "RemovalConfirmedEmptyReadyForLoading", RetainedIntendedStage: MaintenanceStage.AwaitLoading),
                    _ => NeedsInspection("InvalidActionForAwaitRemoval", currentStage)
                }
                : NeedsInspection("SlotExpectedEmptyButDetected", MaintenanceStage.AwaitRemoval),

            MaintenanceStage.AwaitLoading => state == 1
                ? new(MaintenanceStage.Completed, "LoadingConfirmedFlowerPresent")
                : NeedsInspection("SlotExpectedFlowerButEmpty", MaintenanceStage.AwaitLoading),

            _ => new(currentStage, "NoTransitionFromCurrentStage", RetainedIntendedStage: currentStage)
        };
    }

    public static StageTransitionResult EvaluateInspectionRecovery(
        MaintenanceAction action,
        MaintenanceStage intendedStage,
        int relayChannel,
        DateTimeOffset priorInspectionObservedAt,
        DoorCycleEvidence? cycle,
        SensorObservation? newObservation,
        DateTimeOffset now,
        bool kioskOnline = true)
    {
        if (intendedStage is not (MaintenanceStage.AwaitRemoval or MaintenanceStage.AwaitLoading))
            return NeedsInspection("InvalidIntendedStageForRecovery", intendedStage);

        if (newObservation is not null && newObservation.ObservedAt <= priorInspectionObservedAt)
            return NeedsInspection("ObservationNotNewerThanPriorInspection", intendedStage);

        return EvaluateStageCompletion(action, intendedStage, relayChannel, cycle, newObservation, now, kioskOnline);
    }

    public static RetryDecision EvaluateRetry(
        SlotMaintenanceContext context,
        MaintenanceStage currentStage,
        bool doorOpenedInCurrentStage,
        DateTimeOffset lastIssuedAt,
        int currentAttempts,
        DateTimeOffset tokenExpiresAt,
        DateTimeOffset now)
    {
        if (currentAttempts != 1)
            return new(false, currentAttempts, currentAttempts < 1 ? "MalformedRetryAttempts" : "MaxRetryAttemptsExceeded");

        if (lastIssuedAt <= DateTimeOffset.UnixEpoch || tokenExpiresAt <= DateTimeOffset.UnixEpoch)
            return new(false, currentAttempts, "InvalidTokenTimestamps");
        if (tokenExpiresAt <= lastIssuedAt)
            return new(false, currentAttempts, "TokenExpiresBeforeOrAtIssued");
        if (now < lastIssuedAt)
            return new(false, currentAttempts, "ClockSkewNowBeforeLastIssued");
        if (now >= tokenExpiresAt)
            return new(false, currentAttempts, "TokenExpired");
        if (now < lastIssuedAt.AddSeconds(RetryCooldownSeconds))
            return new(false, currentAttempts, "RetryCooldownActive");

        if (!context.KioskOnline) return new(false, currentAttempts, "KioskOffline");
        if (context.LastHeartbeatAt is null || now - context.LastHeartbeatAt.Value > TimeSpan.FromSeconds(TelemetryFreshnessSeconds))
            return new(false, currentAttempts, "KioskHeartbeatStale");
        if (context.LastHeartbeatAt.Value > now.AddSeconds(TelemetryMaxFutureSeconds))
            return new(false, currentAttempts, "KioskHeartbeatFuture");

        if (!context.SupportsManualRetry) return new(false, currentAttempts, "ManualRetryNotSupportedByFirmware");
        if (!context.SensorCapabilityEnabled) return new(false, currentAttempts, "SensorCapabilityDisabled");
        if (!context.SensorInstalled) return new(false, currentAttempts, "SensorNotInstalled");
        if (context.RelayChannel is < 0 or > 3) return new(false, currentAttempts, "InvalidRelayChannel");
        if (context.Zone != "WALK_IN") return new(false, currentAttempts, "SlotNotInWalkInZone");

        if (!context.IsActorActive) return new(false, currentAttempts, "ActorNotActive");
        if (!context.IsShopActive) return new(false, currentAttempts, "ShopNotActive");
        if (context.ActorRole != "SELLER") return new(false, currentAttempts, "ActorRoleRevoked");
        if (context.ActorId == Guid.Empty || context.SellerId == Guid.Empty) return new(false, currentAttempts, "InvalidActorOrSeller");
        if (context.SlotCurrentSellerId != context.SellerId) return new(false, currentAttempts, "SlotOwnershipChanged");
        if (context.KioskSellerId != context.SellerId) return new(false, currentAttempts, "KioskOwnershipChanged");
        if (!context.HasActiveAssignment) return new(false, currentAttempts, "SlotAssignmentRevoked");

        if (context.HasActiveCustomerOrder) return new(false, currentAttempts, "SlotHasActiveCustomerOrder");
        if (context.HasActiveHold) return new(false, currentAttempts, "SlotHasActiveHold");

        if (currentStage is not (MaintenanceStage.AwaitRemoval or MaintenanceStage.AwaitLoading))
            return new(false, currentAttempts, "RetryNotPermittedInStage");
        if (doorOpenedInCurrentStage)
            return new(false, currentAttempts, "DoorAlreadyOpenedInStage");

        if (currentStage == MaintenanceStage.AwaitLoading &&
            (!context.IsLeasePaid || !context.IsProductActive || !context.IsProductOwnedBySeller))
            return new(false, currentAttempts, "LoadingEntitlementRevoked");

        return new(true, 2, null);
    }

    private static StageTransitionResult NeedsInspection(string reason, MaintenanceStage retainedStage) =>
        new(MaintenanceStage.NeedsInspection, reason, InspectionReason: reason, RetainedIntendedStage: retainedStage);
}
