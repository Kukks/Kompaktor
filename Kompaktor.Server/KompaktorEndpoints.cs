using Kompaktor.Contracts;
using Kompaktor.Credentials;
using Kompaktor.Errors;
using Kompaktor.Models;

namespace Kompaktor.Server;

/// <summary>
/// Maps Kompaktor coordinator API endpoints to ASP.NET Core Minimal API routes.
/// </summary>
public static class KompaktorEndpoints
{
    public static void MapKompaktorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/round/{roundId}")
            .WithTags("Kompaktor Round");

        group.MapPost("/pre-register-input", async (string roundId, RegisterInputQuoteRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.PreRegisterInput(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/register-input", async (string roundId, RegisterInputRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.RegisterInput(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/reissue-credentials", async (string roundId, CredentialReissuanceRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.ReissueCredentials(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/register-output", async (string roundId, RegisterOutputRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.RegisterOutput(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/sign", async (string roundId, SignRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.Sign(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/ready-to-sign", async (string roundId, ReadyToSignRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                await op.ReadyToSign(request);
                return Results.Ok();
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        group.MapPost("/send-message", async (string roundId, MessageRequest request, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            try
            {
                var result = await op.SendMessage(request);
                return Results.Ok(result);
            }
            catch (KompaktorProtocolException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
            }
        });

        // Round management endpoints
        app.MapGet("/api/rounds", (KompaktorRoundManager manager) =>
        {
            return Results.Ok(manager.GetActiveRounds());
        }).WithTags("Round Management");

        app.MapGet("/api/round/{roundId}/status", (string roundId, KompaktorRoundManager manager) =>
        {
            var op = manager.GetOperator(roundId);
            if (op is null) return Results.NotFound(new { error = "Round not found" });
            return Results.Ok(new
            {
                roundId,
                status = op.Status.ToString(),
                inputCount = op.Inputs.Count,
                outputCount = op.Outputs.Count,
                signatureCount = op.SignatureCount
            });
        }).WithTags("Round Management");
    }
}
