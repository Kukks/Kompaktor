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

    public Task<KompaktorRoundEvent> GetEvents(string lastEventId)
    {
        throw new NotImplementedException("GetEvents over HTTP is not yet implemented");
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
