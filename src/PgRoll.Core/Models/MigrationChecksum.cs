using System.Security.Cryptography;
using System.Text;

namespace PgRoll.Core.Models;

public static class MigrationChecksum
{
    public static string ComputeSha256(string migrationJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(migrationJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
