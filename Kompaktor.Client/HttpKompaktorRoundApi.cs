using System.Net.Http.Headers;
using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Errors;
using Kompaktor.Models;
using Kompaktor.JsonConverters;

namespace Kompaktor.Client;

/// <summary>
/// HTTP client implementation of IKompaktorRoundApi that communicates with a Kompaktor coordinator server.
/// Each instance uses its own HttpClient (and thus its own circuit when using ICircuitFactory).
/// </summary>
public class HttpKompaktorRoundApi : IKompaktorRoundApi, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _roundId;

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    public HttpKompaktorRoundApi(HttpClient httpClient, string roundId)
    {
        _httpClient = httpClient;
        _roundId = roundId;
    }

    public async Task<KompaktorRoundEvent> GetEvents(string lastEventId)
    {
        // This method is not typically called directly — RemoteKompaktorRound uses GetEventsSince instead.
        var events = await GetEventsSinceAsync(lastEventId);
        return events.FirstOrDefault() ?? throw new InvalidOperationException("No events available");
    }

    /// <summary>
    /// Fetches all round events since a given checkpoint.
    /// Used by RemoteKompaktorRound to poll for state updates.
    /// </summary>
    public async Task<KompaktorRoundEvent[]> GetEventsSinceAsync(string? since = null)
    {
        var url = $"/api/round/{_roundId}/events";
        if (since is not null) url += $"?since={Uri.EscapeDataString(since)}";

        using var response = await _httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new KompaktorProtocolException(
                KompaktorProtocolErrorCode.InternalError,
                $"HTTP {response.StatusCode}: {errorBody}",
                _roundId);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        return KompaktorJsonHelper.DeserializeFromBytes<KompaktorRoundEvent[]>(responseBytes) ?? [];
    }

    public async Task<RoundInfoResponse> GetRoundInfo()
    {
        using var response = await _httpClient.GetAsync($"/api/round/{_roundId}/info");

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new KompaktorProtocolException(
                KompaktorProtocolErrorCode.InternalError,
                $"HTTP {response.StatusCode}: {errorBody}",
                _roundId);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var result = KompaktorJsonHelper.DeserializeFromBytes<RoundInfoResponse>(responseBytes);
        return result ?? throw new KompaktorProtocolException(
            KompaktorProtocolErrorCode.InternalError,
            "Null response from server",
            _roundId);
    }

    public async Task<KompaktorRoundEventMessage> SendMessage(MessageRequest request)
    {
        return await PostAsync<MessageRequest, KompaktorRoundEventMessage>($"/api/round/{_roundId}/send-message", request);
    }

    public async Task<InputRegistrationQuoteResponse> PreRegisterInput(RegisterInputQuoteRequest quoteRequest)
    {
        return await PostAsync<RegisterInputQuoteRequest, InputRegistrationQuoteResponse>($"/api/round/{_roundId}/pre-register-input", quoteRequest);
    }

    public async Task<KompaktorRoundEventInputRegistered> RegisterInput(RegisterInputRequest request)
    {
        return await PostAsync<RegisterInputRequest, KompaktorRoundEventInputRegistered>($"/api/round/{_roundId}/register-input", request);
    }

    public bool Connect(string secret)
    {
        // HTTP clients use WebSocket for persistent connection — not a simple POST.
        // The WebSocket connection is managed separately by the client.
        throw new NotSupportedException("Use WebSocket connection instead");
    }

    public void Disconnect(string secret)
    {
        // Disconnection happens when the WebSocket closes.
        throw new NotSupportedException("Use WebSocket connection instead");
    }

    public async Task<KompaktorRoundCredentialReissuanceResponse> ReissueCredentials(CredentialReissuanceRequest request)
    {
        return await PostAsync<CredentialReissuanceRequest, KompaktorRoundCredentialReissuanceResponse>($"/api/round/{_roundId}/reissue-credentials", request);
    }

    public async Task<KompaktorRoundEventOutputRegistered> RegisterOutput(RegisterOutputRequest request)
    {
        return await PostAsync<RegisterOutputRequest, KompaktorRoundEventOutputRegistered>($"/api/round/{_roundId}/register-output", request);
    }

    public async Task<KompaktorRoundEventSignaturePosted> Sign(SignRequest request)
    {
        return await PostAsync<SignRequest, KompaktorRoundEventSignaturePosted>($"/api/round/{_roundId}/sign", request);
    }

    public async Task ReadyToSign(ReadyToSignRequest request)
    {
        await PostAsync<ReadyToSignRequest>($"/api/round/{_roundId}/ready-to-sign", request);
    }

    public async Task<BatchResponse<InputRegistrationQuoteResponse>> BatchPreRegisterInput(
        BatchPreRegisterInputRequest request)
    {
        return await PostAsync<BatchPreRegisterInputRequest, BatchResponse<InputRegistrationQuoteResponse>>(
            $"/api/round/{_roundId}/batch-pre-register-input", request);
    }

    public async Task<BatchResponse<KompaktorRoundEventInputRegistered>> BatchRegisterInput(
        BatchRegisterInputRequest request)
    {
        return await PostAsync<BatchRegisterInputRequest, BatchResponse<KompaktorRoundEventInputRegistered>>(
            $"/api/round/{_roundId}/batch-register-input", request);
    }

    public async Task<BatchResponse<KompaktorRoundEventSignaturePosted>> BatchSign(BatchSignRequest request)
    {
        return await PostAsync<BatchSignRequest, BatchResponse<KompaktorRoundEventSignaturePosted>>(
            $"/api/round/{_roundId}/batch-sign", request);
    }

    public async Task BatchReadyToSign(BatchReadyToSignRequest request)
    {
        await PostAsync<BatchReadyToSignRequest>($"/api/round/{_roundId}/batch-ready-to-sign", request);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request)
    {
        var content = new ByteArrayContent(KompaktorJsonHelper.SerializeToUtf8Bytes(request));
        content.Headers.ContentType = JsonMediaType;
        using var response = await _httpClient.PostAsync(path, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new KompaktorProtocolException(
                KompaktorProtocolErrorCode.InternalError,
                $"HTTP {response.StatusCode}: {errorBody}",
                _roundId);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var result = KompaktorJsonHelper.DeserializeFromBytes<TResponse>(responseBytes);
        return result ?? throw new KompaktorProtocolException(
            KompaktorProtocolErrorCode.InternalError,
            "Null response from server",
            _roundId);
    }

    private async Task PostAsync<TRequest>(string path, TRequest request)
    {
        var content = new ByteArrayContent(KompaktorJsonHelper.SerializeToUtf8Bytes(request));
        content.Headers.ContentType = JsonMediaType;
        using var response = await _httpClient.PostAsync(path, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new KompaktorProtocolException(
                KompaktorProtocolErrorCode.InternalError,
                $"HTTP {response.StatusCode}: {errorBody}",
                _roundId);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
