#:package Microsoft.Data.SqlClient@6.1.7
// Patched version of a vulnerable transitive dependency of SqlClient.
#:package System.Security.Cryptography.Pkcs@10.0.14

// Grants the API's managed identity read/write access to the database. Safe to re-run.
// Runs as whoever is signed in to the Azure CLI (in the pipeline, the GitHub deploy identity).
//
// Usage: dotnet run infra/grant-db-access.cs -- <server-fqdn> <database> <identity-name> <identity-client-id>

using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: dotnet run infra/grant-db-access.cs -- <server-fqdn> <database> <identity-name> <identity-client-id>");
    return 1;
}

var (server, database, identityName, clientId) = (args[0], args[1], args[2], Guid.Parse(args[3]));

// The name goes into SQL as an identifier (which can't be parameterized), so only allow Azure resource name characters.
if (!Regex.IsMatch(identityName, "^[A-Za-z0-9_-]{1,128}$"))
{
    Console.Error.WriteLine($"Unexpected identity name '{identityName}'.");
    return 1;
}

var connectionString = new SqlConnectionStringBuilder
{
    DataSource = $"tcp:{server},1433",
    InitialCatalog = database,
    Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault,
    Encrypt = SqlConnectionEncryptOption.Mandatory,
    ConnectTimeout = 60,
}.ConnectionString;

// Creating the user from its client ID (as the SID) means SQL doesn't need Microsoft Graph permissions to look it up.
var sid = "0x" + Convert.ToHexString(clientId.ToByteArray());
var sql = $"""
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{identityName}')
        CREATE USER [{identityName}] WITH SID = {sid}, TYPE = E;
    ALTER ROLE db_datareader ADD MEMBER [{identityName}];
    ALTER ROLE db_datawriter ADD MEMBER [{identityName}];
    """;

const int maxAttempts = 6;
for (var attempt = 1; ; attempt++)
{
    try
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        Console.WriteLine($"Granted {identityName} read/write access to {database}.");
        return 0;
    }
    catch (SqlException ex) when (attempt < maxAttempts)
    {
        // A serverless database that's resuming from auto-pause refuses connections for up to a minute.
        Console.WriteLine($"Attempt {attempt} failed (error {ex.Number}); retrying in 20 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(20));
    }
}
