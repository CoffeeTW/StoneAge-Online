using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Protocol;
using StoneAge.Network.Server;
using StoneAge.Server.Security;

namespace StoneAge.Server.Network;

public sealed class LoginPacketHandler(
    IDbContextFactory<GameDbContext> dbFactory,
    Pbkdf2PasswordHasher passwordHasher,
    ILogger<LoginPacketHandler> logger) : IClientPacketHandler
{
    public async Task HandleAsync(GameSession session, PacketFrame packet, NetworkStream stream, CancellationToken cancellationToken)
    {
        if (packet.Opcode != Opcode.LoginRequest)
            return;

        if (session.IsAuthenticated)
        {
            await SendLoginResponseAsync(stream, false, 0, "Already authenticated.", cancellationToken);
            return;
        }

        if (!TryReadLogin(packet.Payload, out var username, out var password))
        {
            await SendLoginResponseAsync(stream, false, 0, "Invalid login packet.", cancellationToken);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Username == username, cancellationToken);

        if (account is null || account.Status != 0 || !passwordHasher.Verify(password, account.PasswordHash))
        {
            logger.LogWarning("Login failed for {Username}", username);
            await SendLoginResponseAsync(stream, false, 0, "Invalid username or password.", cancellationToken);
            return;
        }

        session.Authenticate(account.Id);
        account.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Login success AccountId={AccountId} SessionId={SessionId}", account.Id, session.SessionId);
        await SendLoginResponseAsync(stream, true, account.Id, "Login successful.", cancellationToken);
    }

    private static bool TryReadLogin(byte[] payload, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        if (payload.Length < 4)
            return false;

        var offset = 0;
        var usernameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        offset += 2;
        if (usernameLength is 0 or > 32 || offset + usernameLength + 2 > payload.Length)
            return false;

        username = Encoding.UTF8.GetString(payload, offset, usernameLength);
        offset += usernameLength;

        var passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        offset += 2;
        if (passwordLength is 0 or > 128 || offset + passwordLength != payload.Length)
            return false;

        password = Encoding.UTF8.GetString(payload, offset, passwordLength);
        return true;
    }

    private static async Task SendLoginResponseAsync(
        NetworkStream stream,
        bool success,
        long accountId,
        string message,
        CancellationToken cancellationToken)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var payload = new byte[1 + 8 + 2 + messageBytes.Length];
        payload[0] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1, 8), accountId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9, 2), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(payload.AsSpan(11));

        await stream.WriteAsync(PacketCodec.Encode(Opcode.LoginResponse, payload), cancellationToken);
    }
}
