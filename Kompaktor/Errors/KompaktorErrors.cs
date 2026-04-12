using System.Diagnostics.CodeAnalysis;

namespace Kompaktor.Errors;

/// <summary>
/// Error codes for Kompaktor protocol operations.
/// </summary>
public enum KompaktorProtocolErrorCode
{
	// Round state errors
	WrongPhase,
	RoundAlreadyStarted,
	RoundNotFound,

	// Input registration errors
	InputAlreadyRegistered,
	InputNotValid,
	InputAmountOutOfRange,
	InputCountExceeded,
	InvalidOwnershipProof,
	InsufficientInputFee,
	InvalidQuote,

	// Credential errors
	InvalidCredentialRequest,
	CredentialAmountMismatch,
	InsufficientCredentialBalance,
	CredentialIssuanceFailed,

	// Output registration errors
	OutputNotNegative,
	OutputUneconomic,
	OutputRegistrationFailed,

	// Signing errors
	SignatureAlreadyProvided,
	InputNotFound,
	InputSizeTooLarge,
	InvalidSignature,

	// Messaging errors
	MessagingNotAllowed,

	// Ready to sign errors
	SecretNotFound,

	// Script type errors
	ScriptTypeNotAllowed,

	// Blame round errors
	InputNotWhitelisted,

	// Abuse prevention
	InputBanned,
	RateLimitExceeded,

	// General
	InternalError
}

/// <summary>
/// Base exception for all Kompaktor protocol errors.
/// </summary>
public class KompaktorProtocolException : Exception
{
	public KompaktorProtocolErrorCode ErrorCode { get; }
	public string? RoundId { get; }

	public KompaktorProtocolException(KompaktorProtocolErrorCode errorCode, string message, string? roundId = null, Exception? innerException = null)
		: base(message, innerException)
	{
		ErrorCode = errorCode;
		RoundId = roundId;
	}
}

/// <summary>
/// Represents the result of an operation that can succeed with a value or fail with a protocol exception.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly struct OperationResult<T>
{
	[MemberNotNullWhen(true, nameof(Value))]
	[MemberNotNullWhen(false, nameof(Error))]
	public bool IsSuccess { get; }

	public T? Value { get; }
	public KompaktorProtocolException? Error { get; }

	private OperationResult(T value)
	{
		IsSuccess = true;
		Value = value;
		Error = null;
	}

	private OperationResult(KompaktorProtocolException error)
	{
		IsSuccess = false;
		Value = default;
		Error = error;
	}

	public static OperationResult<T> Success(T value) => new(value);

	public static OperationResult<T> Failure(KompaktorProtocolException error) => new(error);

	public static implicit operator OperationResult<T>(T value) => Success(value);
}
