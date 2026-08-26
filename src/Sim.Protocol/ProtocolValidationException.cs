namespace Sim.Protocol;

/// <summary>
/// Thrown by <see cref="ProtocolValidator.EnsureValid{T}"/> when a protocol
/// message fails structural validation.
/// </summary>
public sealed class ProtocolValidationException : Exception
{
    /// <summary>The type of message that failed validation.</summary>
    public Type MessageType { get; }

    /// <summary>All validation errors that were collected.</summary>
    public IReadOnlyList<string> Errors { get; }

    public ProtocolValidationException(Type messageType, IReadOnlyList<string> errors)
        : base(BuildMessage(messageType, errors))
    {
        MessageType = messageType;
        Errors = errors;
    }

    private static string BuildMessage(Type messageType, IReadOnlyList<string> errors)
        => $"{messageType.Name} failed protocol validation: {string.Join("; ", errors)}";
}

/// <summary>Helpers for validating protocol messages.</summary>
public static class ProtocolValidator
{
    /// <summary>Validates a message and throws <see cref="ProtocolValidationException"/> on failure.</summary>
    public static void EnsureValid<TMessage>(TMessage message)
        where TMessage : IProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        var errors = message.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new ProtocolValidationException(message.GetType(), errors);
        }
    }

    /// <summary>Returns true when the message passes validation.</summary>
    public static bool IsValid<TMessage>(TMessage message)
        where TMessage : IProtocolMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        return !message.Validate().Any();
    }
}
