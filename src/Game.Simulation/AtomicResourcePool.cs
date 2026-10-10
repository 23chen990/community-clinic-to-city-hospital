namespace Game.Simulation;

/// <summary>
/// A deterministic, all-or-nothing pool for finite resources.
///
/// A reservation owns the exact quantity requested for every resource until it
/// is explicitly released or (for soft reservations) expires.  The pool never
/// releases by resource list internally; callers that own a reservation should
/// retain and use its reservation id.
/// </summary>
public sealed class AtomicResourcePool
{
    public const int DefaultSoftReservationTtlTicks = 50;
    public const int DefaultSoftReservationCooldownTicks = 10;

    private readonly Dictionary<string, ResourceState> resources;
    private readonly Dictionary<long, Reservation> reservations = [];
    private readonly Dictionary<string, Queue<long>> legacyReservationIds = [];
    private readonly Dictionary<string, int> retryNotBeforeTicks = [];
    private long nextReservationId = 1;

    public AtomicResourcePool(params (string Id, int Capacity)[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        resources = new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                throw new ArgumentException("资源 ID 不能为空。", nameof(definitions));
            }

            if (definition.Capacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(definitions),
                    definition.Capacity,
                    "资源容量不能为负数。");
            }

            if (!resources.TryAdd(definition.Id, new ResourceState(definition.Capacity)))
            {
                throw new ArgumentException($"资源 ID 重复：{definition.Id}", nameof(definitions));
            }
        }
    }

    public int NegativeResourceCount => resources.Values.Count(resource => resource.Used < 0);

    public IReadOnlyDictionary<string, int> Usage()
    {
        return resources
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Used, StringComparer.Ordinal);
    }

    public IEnumerable<(string Id, int Used, int Capacity)> CanonicalState()
    {
        return resources
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value.Used, entry.Value.Capacity))
            .ToArray();
    }

    /// <summary>
    /// Reserves one unit for each entry in <paramref name="resourceIds"/>.
    /// Duplicate IDs are counted as repeated units.  The operation either owns
    /// the complete request under <paramref name="reservationId"/> or changes
    /// nothing.
    /// </summary>
    public bool TryReserve(IReadOnlyList<string> resourceIds, out long reservationId)
    {
        return TryReserve(
            resourceIds,
            false,
            null,
            out reservationId,
            DefaultSoftReservationTtlTicks,
            DefaultSoftReservationCooldownTicks);
    }

    /// <summary>
    /// Compatibility overload for callers that only need success/failure.  New
    /// callers should retain the explicit reservation ID overload.
    /// </summary>
    public bool TryReserve(IReadOnlyList<string> resourceIds)
    {
        if (!TryReserve(resourceIds, out var reservationId))
        {
            return false;
        }

        RememberLegacyReservation(resourceIds, reservationId);
        return true;
    }

    /// <summary>
    /// Hard reservation with a current Tick.  Running expiry maintenance before
    /// the attempt lets a simulation reclaim soft reservations deterministically
    /// without requiring a separate maintenance call.
    /// </summary>
    public bool TryReserve(
        IReadOnlyList<string> resourceIds,
        int currentTick,
        out long reservationId)
    {
        ValidateTick(currentTick);
        Expire(currentTick);
        return TryReserve(resourceIds, out reservationId);
    }

    public bool TryReserve(
        IReadOnlyDictionary<string, int> requested,
        out long reservationId)
    {
        return TryReserve(
            requested,
            false,
            null,
            out reservationId,
            DefaultSoftReservationTtlTicks,
            DefaultSoftReservationCooldownTicks);
    }

    public bool TryReserveSoft(
        IReadOnlyList<string> resourceIds,
        int currentTick,
        out long reservationId,
        int ttlTicks = DefaultSoftReservationTtlTicks,
        int cooldownTicks = DefaultSoftReservationCooldownTicks)
    {
        ValidateTick(currentTick);
        ValidateSoftTiming(ttlTicks, cooldownTicks);
        Expire(currentTick);

        if (!TryNormalize(resourceIds, out var quantities, out var key))
        {
            reservationId = 0;
            return false;
        }

        if (IsCoolingDown(key, currentTick))
        {
            reservationId = 0;
            return false;
        }

        return TryReserve(
            quantities,
            true,
            currentTick,
            out reservationId,
            ttlTicks,
            cooldownTicks,
            key);
    }

    public bool TryReserveSoft(
        IReadOnlyDictionary<string, int> requested,
        int currentTick,
        out long reservationId,
        int ttlTicks = DefaultSoftReservationTtlTicks,
        int cooldownTicks = DefaultSoftReservationCooldownTicks)
    {
        ValidateTick(currentTick);
        ValidateSoftTiming(ttlTicks, cooldownTicks);
        Expire(currentTick);

        if (!TryNormalize(requested, out var quantities, out var key) || IsCoolingDown(key, currentTick))
        {
            reservationId = 0;
            return false;
        }

        return TryReserve(
            quantities,
            true,
            currentTick,
            out reservationId,
            ttlTicks,
            cooldownTicks,
            key);
    }

    /// <summary>
    /// Releases a reservation exactly once.  A repeated or unknown ID is a
    /// no-op and returns false; either way the resource state remains intact.
    /// </summary>
    public bool Release(long reservationId)
    {
        if (!reservations.Remove(reservationId, out var reservation))
        {
            return false;
        }

        foreach (var (resourceId, quantity) in reservation.Quantities)
        {
            resources[resourceId].Used -= quantity;
        }

        RemoveLegacyReservationReference(reservationId, reservation.Key);
        return true;
    }

    public bool ReleaseReservation(long reservationId) => Release(reservationId);

    /// <summary>
    /// Legacy release adapter for the simulation's existing list-only call
    /// sites.  It releases the oldest still-live reservation made by the
    /// list-only TryReserve overload with the same multiset of resources.
    /// Unknown or already-released lists are no-ops.
    /// </summary>
    public void Release(IReadOnlyList<string> resourceIds)
    {
        if (!TryNormalize(resourceIds, out _, out var key) ||
            !legacyReservationIds.TryGetValue(key, out var ids))
        {
            return;
        }

        while (ids.Count > 0)
        {
            var reservationId = ids.Dequeue();
            if (Release(reservationId))
            {
                break;
            }
        }

        if (ids.Count == 0)
        {
            legacyReservationIds.Remove(key);
        }
    }

    /// <summary>
    /// Releases every soft reservation whose absolute expiry Tick has arrived.
    /// Returns the number of reservations released.  Calling it repeatedly at
    /// the same Tick is idempotent.
    /// </summary>
    public int Expire(int currentTick)
    {
        ValidateTick(currentTick);

        var expired = reservations.Values
            .Where(reservation => reservation.Soft && reservation.ExpiresAtTick <= currentTick)
            .OrderBy(reservation => reservation.Id)
            .ToArray();

        foreach (var reservation in expired)
        {
            if (!Release(reservation.Id))
            {
                continue;
            }

            var retryAt = checked(reservation.ExpiresAtTick + reservation.CooldownTicks);
            if (!retryNotBeforeTicks.TryGetValue(reservation.Key, out var previousRetryAt) ||
                retryAt > previousRetryAt)
            {
                retryNotBeforeTicks[reservation.Key] = retryAt;
            }
        }

        foreach (var key in retryNotBeforeTicks
                     .Where(entry => entry.Value <= currentTick)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            retryNotBeforeTicks.Remove(key);
        }

        return expired.Length;
    }

    private bool TryReserve(
        IReadOnlyList<string> resourceIds,
        bool soft,
        int? currentTick,
        out long reservationId,
        int ttlTicks,
        int cooldownTicks)
    {
        if (!TryNormalize(resourceIds, out var quantities, out var key))
        {
            reservationId = 0;
            return false;
        }

        return TryReserve(
            quantities,
            soft,
            currentTick,
            out reservationId,
            ttlTicks,
            cooldownTicks,
            key);
    }

    private bool TryReserve(
        IReadOnlyDictionary<string, int> requested,
        bool soft,
        int? currentTick,
        out long reservationId,
        int ttlTicks,
        int cooldownTicks,
        string? normalizedKey = null)
    {
        if (!TryNormalize(requested, out var quantities, out var key))
        {
            reservationId = 0;
            return false;
        }

        if (soft && currentTick is null)
        {
            throw new ArgumentException("软预约必须提供当前 Tick。", nameof(currentTick));
        }

        if (!CanReserve(quantities))
        {
            reservationId = 0;
            return false;
        }

        var effectiveKey = normalizedKey ?? key;
        var effectiveExpiry = soft
            ? checked(currentTick!.Value + ttlTicks)
            : int.MaxValue;
        reservationId = checked(nextReservationId++);

        foreach (var (resourceId, quantity) in quantities)
        {
            resources[resourceId].Used = checked(resources[resourceId].Used + quantity);
        }

        reservations.Add(
            reservationId,
            new Reservation(
                reservationId,
                quantities,
                effectiveKey,
                soft,
                effectiveExpiry,
                soft ? cooldownTicks : 0));
        return true;
    }

    private bool CanReserve(IReadOnlyDictionary<string, int> quantities)
    {
        foreach (var (resourceId, quantity) in quantities)
        {
            if (!resources.TryGetValue(resourceId, out var resource))
            {
                return false;
            }

            var available = resource.Capacity - resource.Used;
            if (quantity > available)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCoolingDown(string key, int currentTick)
    {
        if (!retryNotBeforeTicks.TryGetValue(key, out var retryAt))
        {
            return false;
        }

        if (currentTick >= retryAt)
        {
            retryNotBeforeTicks.Remove(key);
            return false;
        }

        return true;
    }

    private void RememberLegacyReservation(IReadOnlyList<string> resourceIds, long reservationId)
    {
        if (!TryNormalize(resourceIds, out _, out var key))
        {
            return;
        }

        if (!legacyReservationIds.TryGetValue(key, out var ids))
        {
            ids = new Queue<long>();
            legacyReservationIds.Add(key, ids);
        }

        ids.Enqueue(reservationId);
    }

    private void RemoveLegacyReservationReference(long reservationId, string key)
    {
        if (!legacyReservationIds.TryGetValue(key, out var ids))
        {
            return;
        }

        if (ids.Count == 0)
        {
            legacyReservationIds.Remove(key);
            return;
        }

        var retained = ids.Where(id => id != reservationId).ToArray();
        if (retained.Length == 0)
        {
            legacyReservationIds.Remove(key);
            return;
        }

        legacyReservationIds[key] = new Queue<long>(retained);
    }

    private static bool TryNormalize(
        IReadOnlyList<string>? resourceIds,
        out SortedDictionary<string, int> quantities,
        out string key)
    {
        quantities = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (resourceIds is null || resourceIds.Count == 0)
        {
            key = string.Empty;
            return false;
        }

        foreach (var resourceId in resourceIds)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                key = string.Empty;
                quantities.Clear();
                return false;
            }

            quantities[resourceId] = quantities.TryGetValue(resourceId, out var count)
                ? checked(count + 1)
                : 1;
        }

        key = BuildKey(quantities);
        return true;
    }

    private static bool TryNormalize(
        IReadOnlyDictionary<string, int>? requested,
        out SortedDictionary<string, int> quantities,
        out string key)
    {
        quantities = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (requested is null || requested.Count == 0)
        {
            key = string.Empty;
            return false;
        }

        foreach (var (resourceId, quantity) in requested)
        {
            if (string.IsNullOrWhiteSpace(resourceId) || quantity <= 0)
            {
                key = string.Empty;
                quantities.Clear();
                return false;
            }

            quantities[resourceId] = quantities.TryGetValue(resourceId, out var existing)
                ? checked(existing + quantity)
                : quantity;
        }

        key = BuildKey(quantities);
        return true;
    }

    private static string BuildKey(IEnumerable<KeyValuePair<string, int>> quantities)
    {
        return string.Join(
            "\u001f",
            quantities.Select(entry => $"{entry.Key}\u001e{entry.Value}"));
    }

    private static void ValidateTick(int currentTick)
    {
        if (currentTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentTick), currentTick, "Tick 不能为负数。");
        }
    }

    private static void ValidateSoftTiming(int ttlTicks, int cooldownTicks)
    {
        if (ttlTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ttlTicks), ttlTicks, "软预约 TTL 必须为正数。");
        }

        if (cooldownTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cooldownTicks),
                cooldownTicks,
                "软预约冷却 Tick 不能为负数。");
        }
    }

    private sealed class ResourceState
    {
        public ResourceState(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }
        public int Used { get; set; }
    }

    private sealed record Reservation(
        long Id,
        IReadOnlyDictionary<string, int> Quantities,
        string Key,
        bool Soft,
        int ExpiresAtTick,
        int CooldownTicks);
}
