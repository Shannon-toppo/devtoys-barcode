namespace DevToys.Barcode.Helpers;

/// <summary>
/// Why a barcode could not be generated. The GUI maps each value to a localized message.
/// (Public only so that xUnit theories can take it as a parameter; the other helper types are internal.)
/// </summary>
public enum BarcodeGenerationFailureReason
{
    /// <summary>The input text is empty.</summary>
    EmptyInput,

    /// <summary>The input contains characters that the selected format cannot encode.</summary>
    InvalidCharacters,

    /// <summary>The input has a length that the selected format does not accept (for example EAN-13 needs 12 or 13 digits).</summary>
    WrongLength,

    /// <summary>The input includes a check digit and it is wrong.</summary>
    BadCheckDigit,

    /// <summary>The input does not fit in the selected format.</summary>
    TooLong,

    /// <summary>Any other rejection by the encoder. <see cref="BarcodeGenerationResult{T}.Message"/> carries the underlying message.</summary>
    EncoderRejected
}

/// <summary>
/// Either a generated barcode (<see cref="Value"/>) or the reason why it could not be generated.
/// </summary>
/// <typeparam name="T">The kind of output: <c>Image&lt;Rgba32&gt;</c> or <see cref="string"/> (SVG).</typeparam>
internal sealed class BarcodeGenerationResult<T>
    where T : class
{
    private BarcodeGenerationResult(T? value, string? encodedText, BarcodeGenerationFailureReason? reason, string? message)
    {
        Value = value;
        EncodedText = encodedText;
        Reason = reason;
        Message = message;
    }

    /// <summary>Whether <see cref="Value"/> is available.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Value), nameof(EncodedText))]
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(Reason))]
    public bool IsSuccess => Value is not null;

    /// <summary>The generated output. When it is an image, the caller owns it and must dispose it.</summary>
    public T? Value { get; }

    /// <summary>
    /// The text that was actually encoded. It differs from the input when a check digit was appended
    /// (EAN-13, EAN-8, UPC-A, UPC-E).
    /// </summary>
    public string? EncodedText { get; }

    /// <summary>The failure reason, when <see cref="IsSuccess"/> is <c>false</c>.</summary>
    public BarcodeGenerationFailureReason? Reason { get; }

    /// <summary>The underlying encoder message, when there is one. Not localized; meant for logs and the generic fallback.</summary>
    public string? Message { get; }

    internal static BarcodeGenerationResult<T> Success(T value, string encodedText)
        => new(value, encodedText, null, null);

    internal static BarcodeGenerationResult<T> Failure(BarcodeGenerationFailureReason reason, string? message = null)
        => new(null, null, reason, message);
}
