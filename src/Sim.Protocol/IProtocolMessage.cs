using System.Text.Json.Serialization;

namespace Sim.Protocol;

/// <summary>Well-known role names used as dictionary keys and observation roles.</summary>
public static class RoleNames
{
    public const string Us = "us";
    public const string Them = "them";

    /// <summary>Returns true when <paramref name="role"/> is one of the two known roles.</summary>
    public static bool IsKnownRole(string? role)
        => role == Us || role == Them;
}

/// <summary>
/// Contract implemented by every protocol message DTO. Each message carries a
/// protocol version and knows how to validate itself; an empty
/// <see cref="Validate"/> result means the message is well formed.
/// </summary>
public interface IProtocolMessage
{
    /// <summary>Wire-format version stamped on this message (JSON: "protocolVersion").</summary>
    [JsonPropertyName("protocolVersion")]
    string Version { get; }

    /// <summary>Returns all validation error messages for this message. Empty means valid.</summary>
    IEnumerable<string> Validate();
}
