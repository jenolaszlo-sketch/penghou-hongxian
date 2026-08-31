using System.Text.Json;

namespace Penghou.Hongxian;

/// <summary>Typed and JSON-tree helpers for provider-neutral session payloads.</summary>
public static class SessionEventPayloadExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Appends a CLR payload through a JSON tree. Payload identity therefore
    /// depends on persisted JSON, not on the CLR type used by the caller.
    /// </summary>
    public static Task<SessionEvent> AppendAsync<T>(
        this ISessionEventStore store,
        SessionEventRequest request,
        T payload,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        EnsurePayloadIsUnset(request);
        var element = JsonSerializer.SerializeToElement(
            payload,
            serializerOptions ?? DefaultOptions);
        return store.AppendAsync(
            request with { Payload = element },
            cancellationToken);
    }

    /// <summary>Appends an existing JSON tree using the provider's canonical JSON contract.</summary>
    public static Task<SessionEvent> AppendAsync(
        this ISessionEventStore store,
        SessionEventRequest request,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        EnsurePayloadIsUnset(request);
        return store.AppendAsync(
            request with { Payload = payload.Clone() },
            cancellationToken);
    }

    /// <summary>
    /// Appends a CLR payload and reports projection delivery separately from
    /// the authoritative event commit.
    /// </summary>
    public static Task<SessionEventAppendResult> AppendWithDeliveryAsync<T>(
        this ISessionEventDeliveryStore store,
        SessionEventRequest request,
        T payload,
        JsonSerializerOptions? serializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        EnsurePayloadIsUnset(request);
        var element = JsonSerializer.SerializeToElement(
            payload,
            serializerOptions ?? DefaultOptions);
        return store.AppendWithDeliveryAsync(
            request with { Payload = element },
            cancellationToken);
    }

    /// <summary>
    /// Appends an existing JSON tree and reports projection delivery separately
    /// from the authoritative event commit.
    /// </summary>
    public static Task<SessionEventAppendResult> AppendWithDeliveryAsync(
        this ISessionEventDeliveryStore store,
        SessionEventRequest request,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        EnsurePayloadIsUnset(request);
        return store.AppendWithDeliveryAsync(
            request with { Payload = payload.Clone() },
            cancellationToken);
    }

    /// <summary>Reads a retained payload without requiring its original CLR type.</summary>
    public static JsonElement ReadPayload(this SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (sessionEvent.PayloadRetention != SessionPayloadRetention.Retain)
            throw new SessionPayloadUnavailableException(sessionEvent.PayloadRetention);
        if (sessionEvent.Payload is { } payload)
            return payload.Clone();
        if (sessionEvent.PayloadJson is null)
            throw new SessionPayloadUnavailableException(sessionEvent.PayloadRetention);
        try
        {
            using var document = JsonDocument.Parse(sessionEvent.PayloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new SessionPayloadFormatException(sessionEvent.EventId, exception);
        }
    }

    /// <summary>Reads and deserializes a retained payload.</summary>
    public static T ReadPayload<T>(
        this SessionEvent sessionEvent,
        JsonSerializerOptions? serializerOptions = null)
    {
        try
        {
            return sessionEvent.ReadPayload().Deserialize<T>(
                serializerOptions ?? DefaultOptions)
                ?? throw new SessionPayloadFormatException(sessionEvent.EventId);
        }
        catch (JsonException exception)
        {
            throw new SessionPayloadFormatException(sessionEvent.EventId, exception);
        }
    }

    /// <summary>Upcasts a retained payload before typed deserialization.</summary>
    public static T ReadPayload<T>(
        this SessionEvent sessionEvent,
        SessionPayloadSchema targetSchema,
        SessionPayloadUpcasterRegistry upcasters,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(targetSchema);
        ArgumentNullException.ThrowIfNull(upcasters);
        var source = sessionEvent.PayloadSchema
            ?? throw new UnsupportedSessionPayloadSchemaException(
                new SessionPayloadSchema(targetSchema.Name, 1),
                targetSchema.Version,
                1);
        if (!string.Equals(source.Name, targetSchema.Name, StringComparison.Ordinal))
            throw new UnsupportedSessionPayloadSchemaException(
                source,
                targetSchema.Version,
                source.Version);
        var upcasted = upcasters.Upcast(
            source,
            sessionEvent.ReadPayload(),
            targetSchema.Version);
        try
        {
            return upcasted.Payload.Deserialize<T>(serializerOptions ?? DefaultOptions)
                ?? throw new SessionPayloadFormatException(sessionEvent.EventId);
        }
        catch (JsonException exception)
        {
            throw new SessionPayloadFormatException(sessionEvent.EventId, exception);
        }
    }

    private static void EnsurePayloadIsUnset(SessionEventRequest request)
    {
        if (request.Payload is not null || request.PayloadJson is not null)
            throw new ArgumentException(
                "The request already contains a payload.",
                nameof(request));
    }
}

/// <summary>Raised when retention intentionally made payload content unavailable.</summary>
public sealed class SessionPayloadUnavailableException(SessionPayloadRetention retention)
    : Exception($"Session payload content is unavailable under retention policy '{retention}'.")
{
    public SessionPayloadRetention Retention { get; } = retention;
}

/// <summary>Raised when retained payload JSON cannot be read as the requested value.</summary>
public sealed class SessionPayloadFormatException(Guid eventId, Exception? innerException = null)
    : Exception($"Session event '{eventId:D}' contains an unreadable payload.", innerException)
{
    public Guid EventId { get; } = eventId;
}
