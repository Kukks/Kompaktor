using System.Text.Json.Serialization;
using Kompaktor.Contracts;
using Kompaktor.Errors;

namespace Kompaktor.Models;

/// <summary>
/// Batch sign request — submits multiple input signatures in a single call.
/// Reduces round-trips from N (one per input) to 1 per client.
/// </summary>
public record BatchSignRequest
{
    [JsonPropertyName("requests")]
    public required SignRequest[] Requests { get; init; }
}

/// <summary>
/// Result of a single item within a batch operation.
/// </summary>
public record BatchItemResult<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    public static BatchItemResult<T> Ok(T result) => new()
    {
        Success = true,
        Result = result
    };

    public static BatchItemResult<T> Fail(KompaktorProtocolException ex) => new()
    {
        Success = false,
        Error = ex.Message,
        ErrorCode = ex.ErrorCode.ToString()
    };

    public static BatchItemResult<T> Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}

/// <summary>
/// Batch response wrapping per-item results.
/// </summary>
public record BatchResponse<T>
{
    [JsonPropertyName("results")]
    public required BatchItemResult<T>[] Results { get; init; }

    [JsonPropertyName("successCount")]
    public int SuccessCount => Results.Count(r => r.Success);

    [JsonPropertyName("failureCount")]
    public int FailureCount => Results.Count(r => !r.Success);
}

/// <summary>
/// Batch ready-to-sign request — signals readiness for multiple inputs in one call.
/// </summary>
public record BatchReadyToSignRequest
{
    [JsonPropertyName("secrets")]
    public required string[] Secrets { get; init; }
}

/// <summary>
/// Batch pre-register input request — submits multiple ownership proofs in one call.
/// </summary>
public record BatchPreRegisterInputRequest
{
    [JsonPropertyName("requests")]
    public required RegisterInputQuoteRequest[] Requests { get; init; }
}

/// <summary>
/// Batch register input request — completes registration for multiple quoted inputs.
/// </summary>
public record BatchRegisterInputRequest
{
    [JsonPropertyName("requests")]
    public required RegisterInputRequest[] Requests { get; init; }
}
