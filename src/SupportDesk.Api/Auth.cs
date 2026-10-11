using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportDesk.Core;
namespace SupportDesk.Api;

public sealed record PasswordRecord(string Salt, string Hash, int Iterations = 210000);
public sealed class Auth(Store store, PasswordRecord password)
{
    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static PasswordRecord CreatePassword(string value)
    {
        if (value.Length < 12)
            throw new ArgumentException("Minimum 12 characters");
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(value, salt, 210000, HashAlgorithmName.SHA256, 32)));
    }
    public bool Check(string value)
    {
        if (value.Length > 512)
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(password.Salt), password.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(password.Hash));
    }
    static (SessionTokens Tokens, string Access, string Refresh, long AccessUntil, long RefreshUntil) Tokens()
    {
        var a = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var r = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        return (new(a, r, expires), Hash(a), Hash(r), expires.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
    }
    public SessionTokens Login(string device)
    {
        var t = Tokens();
        using var c = store.Open();
        Store.Run(c, "DELETE FROM sessions WHERE refresh_until<@now; INSERT INTO sessions VALUES(@id,@a,@r,@au,@ru,@device)", ("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("@id", Guid.NewGuid().ToString("N")), ("@a", t.Access), ("@r", t.Refresh), ("@au", t.AccessUntil), ("@ru", t.RefreshUntil), ("@device", device[..Math.Min(device.Length, 100)]));
        return t.Tokens;
    }
    public bool Authorized(string token)
    {
        if (token.Length != 64)
            return false;
        using var c = store.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sessions WHERE access=@a AND access_until>@now AND refresh_until>@now";
        cmd.Parameters.AddWithValue("@a", Hash(token));
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return cmd.ExecuteScalar() is not null;
    }
    public SessionTokens? Refresh(string token)
    {
        if (token.Length != 64)
            return null;
        using var c = store.Open();
        using var tx = store.BeginWrite(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM sessions WHERE refresh=@r AND refresh_until>@now" + (store.IsPostgres ? " FOR UPDATE" : "");
        cmd.Parameters.AddWithValue("@r", Hash(token));
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var id = cmd.ExecuteScalar();
        if (id is null)
            return null;
        var t = Tokens();
        Store.Run(c, "UPDATE sessions SET access=@a,refresh=@r,access_until=@au,refresh_until=@ru WHERE id=@id", ("@a", t.Access), ("@r", t.Refresh), ("@au", t.AccessUntil), ("@ru", t.RefreshUntil), ("@id", id));
        tx.Commit();
        return t.Tokens;
    }
    public void Logout(string token)
    {
        using var c = store.Open();
        Store.Run(c, "DELETE FROM sessions WHERE access=@a", ("@a", Hash(token)));
    }
}

