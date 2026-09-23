namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

internal static class RepositoryPaths
{
    public static string PostgresInitScript =>
        Path.Combine(RepositoryRoot, "infra", "docker", "postgres", "init", "01-create-databases.sh");

    public static string LedgerApiAppSettings =>
        Path.Combine(RepositoryRoot, "services", "ledger-api", "src", "LedgerCore.Ledger.Api", "appsettings.json");

    private static string RepositoryRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Could not locate the repository root (docker-compose.yml).");
        }
    }
}
