namespace LedgerCore.Ledger.Api.Configuration;

/// <summary>Binds and validates the ledger's options. An invalid value stops the host at startup.</summary>
internal static class LedgerOptionsRegistration
{
    public static IServiceCollection AddLedgerOptions(this IServiceCollection services)
    {
        services
            .AddOptions<LedgerApiOptions>()
            .BindConfiguration(LedgerApiOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<LedgerDatabaseOptions>()
            .BindConfiguration(LedgerDatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }
}
