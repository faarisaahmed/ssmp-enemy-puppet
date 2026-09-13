using SSMP.Logging;

namespace SSMPEnemyPuppet;

/// <summary>
/// Thin static wrapper over the logger SSMP hands to addons, so static helpers do not have to
/// carry a logger reference around.
/// </summary>
internal static class Log {
    private static ILogger _logger;

    private const string Prefix = "[EnemyPuppet] ";

    public static void Initialize(ILogger logger) => _logger = logger;

    public static void Debug(string message) => _logger?.Debug(Prefix + message);

    public static void Info(string message) => _logger?.Info(Prefix + message);

    public static void Warn(string message) => _logger?.Warn(Prefix + message);

    public static void Error(string message) => _logger?.Error(Prefix + message);
}
