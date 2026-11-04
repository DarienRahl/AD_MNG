namespace AdMng.Watchdog.Contracts;

/// <summary>
/// A collection property that is left out of the JSON while it is empty (<see cref="WatchdogJson.Options"/>). Used for a
/// member a newer client adds to a request type that rejects unknown members: an older service still accepts the request
/// as long as the member is not used (U1c: <c>UserMessageRequest.inlineImages</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
internal sealed class JsonOmitWhenEmptyAttribute : Attribute;
